import { createMockStore, createRemoteStore, formatTime, statusLabel, type AppState, type DeviceAxisSetting, type MediaSourceInput, type OutputTargetInput, type Route, type RuntimeSourceRoute, type ScriptMatchMode, type ScriptResource, type Status, type StoreChange } from './store';
import { ApiError, MfpApiClient, type ApiMediaServerDiscovery } from './api';
import { authorizeFnosScriptFolder, clearPendingFnosScriptAuthorization, consumeFnosAuthorizationCallback, fnosPendingAuthorizationKey, hasPendingFnosScriptAuthorization, openFnosAppSettings } from './fnos';
import { destroyOsrAxisPreview, modeledAxisNames, mountOsrAxisPreview, updateOsrAxisPreview, type ModeledAxisName } from './osr-axis-preview';
import './styles.css';

const app = document.querySelector<HTMLDivElement>('#app');
if (!app) throw new Error('App root is missing');
const appRoot = app;

const demoStore = createMockStore();
const apiClient = new MfpApiClient();
let store = demoStore;
let unsubscribeStore: () => void = () => {};
let renderedRoute: Route | null = null;
let statusRenderTimer: number | undefined;
let demoTimer: number | undefined;
let runtimePollTimer: number | undefined;
let runtimePollActive = false;

function syncRuntimeWork(state: AppState): void {
  const active = !document.hidden && state.route === 'runtime';
  const animateDemo = active && state.mode === 'demo' && state.media.some(source => source.mediaState?.playing);
  if (animateDemo && demoTimer === undefined) demoTimer = window.setInterval(() => store.tick(), 100);
  if (!animateDemo && demoTimer !== undefined) { clearInterval(demoTimer); demoTimer = undefined; }
  if (!active || state.mode !== 'server') {
    if (runtimePollTimer !== undefined) clearTimeout(runtimePollTimer);
    runtimePollTimer = undefined;
    return;
  }
  if (runtimePollTimer === undefined && !runtimePollActive) {
    const delay = state.media.some(source => source.mediaState?.playing) ? 500 : 2000;
    runtimePollTimer = window.setTimeout(async () => {
      runtimePollTimer = undefined;
      runtimePollActive = true;
      try { await store.refreshRuntimeStatus(); } catch { /* reconnect status is shown by the store */ }
      finally { runtimePollActive = false; syncRuntimeWork(store.getState()); }
    }, delay);
  }
}

function onStoreChanged(state: AppState, change: StoreChange = 'full'): void {
  syncRuntimeWork(state);
  if (document.hidden) return;
  if (change === 'status') {
    if (statusRenderTimer === undefined) statusRenderTimer = window.setTimeout(() => {
      statusRenderTimer = undefined;
      if (!document.hidden) render(store.getState(), 'status');
    }, 100);
  } else {
    if (statusRenderTimer !== undefined) clearTimeout(statusRenderTimer);
    statusRenderTimer = undefined;
    render(state, change);
  }
}
let routingDrag: { sourceId: string; pointerId: number } | null = null;
let selectedAxisPreview: ModeledAxisName = 'L0';
let selectedAxisTargetId: string | null = null;
let followLiveTarget = false;

interface WizardResult {
  minimum: number;
  maximum: number;
  limitMinimum: number;
  limitMaximum: number;
  limitEnabled: boolean;
}

let wizardState = {
  active: false,
  step: "select-axes" as "select-axes" | "calibrating-min" | "calibrating-max" | "summary",
  selectedAxes: ["L0", "L1", "L2", "R0", "R1", "R2"],
  currentAxisIndex: 0,
  results: {} as Record<string, WizardResult>,
  tempMin: 0.2,
  tempMax: 0.8,
  backupAxes: [] as DeviceAxisSetting[],
};

let wizardPoseThrottleTimer: number | undefined;
let wizardPendingPose: { axis: string; value: number } | null = null;

function syncWizardPose(axisName: string, value: number): void {
  const previewPose: Partial<Record<ModeledAxisName, number>> = {};
  for (const name of modeledAxisNames) {
    previewPose[name] = name === axisName ? value : (axisPreviewPositions[name] ?? 0.5);
  }
  updateOsrAxisPreview(previewPose);

  wizardPendingPose = { axis: axisName, value };
  if (wizardPoseThrottleTimer === undefined) {
    wizardPoseThrottleTimer = window.setTimeout(() => {
      wizardPoseThrottleTimer = undefined;
      if (!wizardPendingPose) return;
      const poseToSend = wizardPendingPose;
      wizardPendingPose = null;
      const state = store.getState();
      const target = state.outputs.find((t) => t.id === selectedAxisTargetId);
      if (target?.status === "online") {
        void apiClient.sendDeviceAxesPose({ [poseToSend.axis]: poseToSend.value }, target.id, 50).catch(() => {});
      }
    }, 40);
  }
}

function applyWizardResultsToForm(results: Record<string, WizardResult>): void {
  const form = currentAxisForm();
  if (!form) return;
  for (const [axisName, res] of Object.entries(results)) {
    const row = form.querySelector<HTMLElement>(`[data-axis-row="${axisName}"]`);
    if (!row) continue;
    const minInput = row.querySelector<HTMLInputElement>('[data-axis-field="minimum"]');
    const maxInput = row.querySelector<HTMLInputElement>('[data-axis-field="maximum"]');
    const limitMinInput = row.querySelector<HTMLInputElement>('[data-axis-field="limitMinimum"]');
    const limitMaxInput = row.querySelector<HTMLInputElement>('[data-axis-field="limitMaximum"]');
    const limitEnabledInput = row.querySelector<HTMLInputElement>('[data-axis-field="limitEnabled"]');
    if (minInput) minInput.value = String(Number(res.minimum.toFixed(2)));
    if (maxInput) maxInput.value = String(Number(res.maximum.toFixed(2)));
    if (limitMinInput) limitMinInput.value = String(Number(res.limitMinimum.toFixed(2)));
    if (limitMaxInput) limitMaxInput.value = String(Number(res.limitMaximum.toFixed(2)));
    if (limitEnabledInput) limitEnabledInput.checked = res.limitEnabled;
    synchronizeAxisRow(row, "minimum");
    synchronizeAxisRow(row, "limitMinimum");
  }
}

function renderCalibrationWizardModal(): string {
  if (!wizardState.active) return "";

  let stepBadge = "";
  let wizardTitle = "";
  let wizardSubtitle = "";
  let bodyHtml = "";
  let footerHtml = "";

  if (wizardState.step === "select-axes") {
    stepBadge = "Step 1 of 3 · Selection";
    wizardTitle = "Select axes to calibrate";
    wizardSubtitle = "Choose which mechanical axes to include in the teach-in calibration wizard.";
    const selectedCount = wizardState.selectedAxes.length;
    const cardsHtml = modeledAxisNames.map((axis) => {
      const checked = wizardState.selectedAxes.includes(axis);
      return `<label class="axis-wizard-axis-card"><input type="checkbox" data-axis-wizard-select="${axis}" ${checked ? "checked" : ""} /><div><strong>${axis}</strong><span>${escapeHtml(axisFriendlyNames[axis] ?? "")}</span></div></label>`;
    }).join("");

    bodyHtml = `
      <div class="axis-wizard-tools">
        <button type="button" class="button button-secondary" data-axis-wizard-preset="6axes">Standard 6 axes</button>
        <button type="button" class="button button-secondary" data-axis-wizard-preset="all">Select all</button>
        <button type="button" class="button button-secondary" data-axis-wizard-preset="none">Clear</button>
      </div>
      <div class="axis-wizard-axes-grid">
        ${cardsHtml}
      </div>
      <p class="axis-wizard-instruction">For each selected axis, you will set the physical minimum, followed by the maximum with a safety threshold barrier.</p>
    `;

    footerHtml = `
      <button type="button" class="button button-secondary" data-axis-wizard-cancel>Cancel</button>
      <button type="button" class="button button-primary" data-axis-wizard-start-calibration ${selectedCount === 0 ? "disabled" : ""}>Start calibration (${selectedCount} axes) &rarr;</button>
    `;
  } else if (wizardState.step === "calibrating-min") {
    const axis = wizardState.selectedAxes[wizardState.currentAxisIndex] ?? "L0";
    const friendly = axisFriendlyNames[axis] ?? "";
    const progress = `Axis ${wizardState.currentAxisIndex + 1} of ${wizardState.selectedAxes.length}`;
    stepBadge = `Step 2 · ${progress} · Minimum`;
    wizardTitle = `Set ${axis} minimum position`;
    wizardSubtitle = `Move slider until ${axis} reaches its lowest/safe inward limit. 3D & device track live.`;

    bodyHtml = `
      <div class="axis-wizard-value-display">
        <span>${axis} · ${escapeHtml(friendly)} Minimum</span>
        <output data-axis-wizard-min-output>${percent(wizardState.tempMin)}</output>
      </div>
      <div class="axis-wizard-slider-wrapper">
        <input type="range" min="0" max="0.95" step="0.01" value="${wizardState.tempMin}" data-axis-wizard-slider="min" aria-label="${axis} minimum limit position" />
      </div>
      <div class="axis-wizard-fine-tune">
        <button type="button" class="button button-secondary" data-axis-wizard-adjust="min,-0.05">-5%</button>
        <button type="button" class="button button-secondary" data-axis-wizard-adjust="min,-0.01">-1%</button>
        <button type="button" class="button button-secondary" data-axis-wizard-adjust="min,0.01">+1%</button>
        <button type="button" class="button button-secondary" data-axis-wizard-adjust="min,0.05">+5%</button>
        <button type="button" class="button button-secondary" data-axis-wizard-set="min,0.2">Default (20%)</button>
      </div>
      <p class="axis-wizard-instruction">This point defines Output Minimum. In the next step, this value becomes the disabled barrier line for Maximum.</p>
    `;

    footerHtml = `
      <button type="button" class="button button-secondary" data-axis-wizard-cancel>Cancel wizard</button>
      <button type="button" class="button button-primary" data-axis-wizard-next="min">Next: Set maximum &rarr;</button>
    `;
  } else if (wizardState.step === "calibrating-max") {
    const axis = wizardState.selectedAxes[wizardState.currentAxisIndex] ?? "L0";
    const friendly = axisFriendlyNames[axis] ?? "";
    const progress = `Axis ${wizardState.currentAxisIndex + 1} of ${wizardState.selectedAxes.length}`;
    const isLast = wizardState.currentAxisIndex >= wizardState.selectedAxes.length - 1;
    stepBadge = `Step 2 · ${progress} · Maximum`;
    wizardTitle = `Set ${axis} maximum position`;
    wizardSubtitle = `Move slider to physical highest safe position. Striped zone shows minimum threshold barrier.`;

    const barrierPercent = (wizardState.tempMin * 100).toFixed(1);
    const sliderMin = Math.min(0.99, wizardState.tempMin + 0.01);

    bodyHtml = `
      <div class="axis-wizard-value-display">
        <span>${axis} · ${escapeHtml(friendly)} Maximum</span>
        <output data-axis-wizard-max-output>${percent(wizardState.tempMax)}</output>
      </div>
      <div class="axis-wizard-slider-wrapper">
        <div class="axis-wizard-barrier-track">
          <div class="axis-wizard-barrier-zone" style="width: ${barrierPercent}%" title="Fixed minimum barrier: ${percent(wizardState.tempMin)}"></div>
          <span class="axis-wizard-barrier-label" style="left: ${barrierPercent}%">Min: ${percent(wizardState.tempMin)}</span>
          <input type="range" min="${sliderMin}" max="1" step="0.01" value="${wizardState.tempMax}" data-axis-wizard-slider="max" aria-label="${axis} maximum limit position" />
        </div>
      </div>
      <div class="axis-wizard-fine-tune">
        <button type="button" class="button button-secondary" data-axis-wizard-adjust="max,-0.05">-5%</button>
        <button type="button" class="button button-secondary" data-axis-wizard-adjust="max,-0.01">-1%</button>
        <button type="button" class="button button-secondary" data-axis-wizard-adjust="max,0.01">+1%</button>
        <button type="button" class="button button-secondary" data-axis-wizard-adjust="max,0.05">+5%</button>
        <button type="button" class="button button-secondary" data-axis-wizard-set="max,1.0">Top (100%)</button>
      </div>
      <div class="axis-wizard-limit-info">
        <span aria-hidden="true">🛡️</span>
        <span>Safety limit auto-adapted to <strong>${percent(Math.max(0, wizardState.tempMin - 0.01))} ~ ${percent(Math.min(1, wizardState.tempMax + 0.01))}</strong> with limit protection enabled.</span>
      </div>
    `;

    footerHtml = `
      <button type="button" class="button button-secondary" data-axis-wizard-prev="max">&larr; Back to minimum</button>
      <button type="button" class="button button-primary" data-axis-wizard-next="max">${isLast ? "Finish & review summary &rarr;" : "Next axis &rarr;"}</button>
    `;
  } else if (wizardState.step === "summary") {
    stepBadge = "Step 3 of 3 · Finalize";
    wizardTitle = "Calibration summary";
    wizardSubtitle = `Verified ranges and limits for ${Object.keys(wizardState.results).length} axes. Choose how to apply.`;

    const rowsHtml = Object.entries(wizardState.results).map(([axis, res]) => {
      const span = res.maximum - res.minimum;
      return `<tr><td><strong>${axis}</strong> <small style="color:var(--subtle)">${escapeHtml(axisFriendlyNames[axis] ?? "")}</small></td><td><span class="rate-value">${percent(res.minimum)} ~ ${percent(res.maximum)}</span></td><td><span style="color:#38bdf8">${percent(res.limitMinimum)} ~ ${percent(res.limitMaximum)}</span></td><td><span class="status-badge status-online">${percent(span)} travel</span></td></tr>`;
    }).join("");

    bodyHtml = `
      <div class="axis-wizard-table-wrap">
        <table class="axis-wizard-table">
          <thead>
            <tr><th>Axis</th><th>Output range</th><th>Safety limit</th><th>Span</th></tr>
          </thead>
          <tbody>
            ${rowsHtml}
          </tbody>
        </table>
      </div>
      <p class="axis-wizard-instruction"><strong>Save draft:</strong> updates the form below so you can inspect before saving.<br/><strong>Save & apply:</strong> permanently persists to server and activates hardware output immediately.</p>
    `;

    footerHtml = `
      <button type="button" class="button button-secondary" data-axis-wizard-cancel>Cancel</button>
      <button type="button" class="button button-secondary" data-axis-wizard-save="draft">Save draft</button>
      <button type="button" class="button button-primary" data-axis-wizard-save="apply">Save & apply to hardware</button>
    `;
  }

  return `
    <div class="axis-wizard-modal" data-axis-wizard-modal role="dialog" aria-modal="true" aria-labelledby="wizard-title">
      <div class="axis-wizard-dialog">
        <div class="axis-wizard-header">
          <div>
            <span class="eyebrow">${stepBadge}</span>
            <h2 id="wizard-title">${wizardTitle}</h2>
            <p class="axis-wizard-subtitle">${wizardSubtitle}</p>
          </div>
          <button type="button" class="axis-wizard-close" data-axis-wizard-cancel aria-label="Close wizard">&times;</button>
        </div>
        <div class="axis-wizard-body">
          ${bodyHtml}
        </div>
        <div class="axis-wizard-footer">
          ${footerHtml}
        </div>
      </div>
    </div>
  `;
}
const axisPreviewPositions: Record<ModeledAxisName, number> = Object.fromEntries(
  modeledAxisNames.map((axis) => [axis, 0.5]),
) as Record<ModeledAxisName, number>;
let axisPreviewSweepTimer: number | undefined;
let axisPreviewSweepStartedAt = 0;
let axisPreviewSimulationTimer: number | undefined;
let axisPreviewSimulationAxisIndex = 0;
let axisPreviewSimulationPhase = 0;
let axisPreviewSimulationLastFrameAt = 0;
let axisPreviewSimulationAxes: ModeledAxisName[] = [];
let axisPreviewSimulationRestore: { axis: ModeledAxisName; positions: Record<ModeledAxisName, number> } | null = null;
const axisPreviewSimulationAxisDuration = 1800;
let selectedScriptFolderPath: string | null = null;
let scriptPreviewExtension = 'all';
let scriptPreviewPage = 1;
const scriptPreviewPageSize = 8;
const importInput = document.createElement('input');
importInput.type = 'file';
importInput.className = 'sr-only';
importInput.accept = '.funscript,.json,application/json';
importInput.setAttribute('aria-label', 'Select a script file to import');
document.body.append(importInput);
const toastRegion = document.createElement('div');
let toastTimer: number | undefined;
toastRegion.className = 'toast-region';
toastRegion.setAttribute('aria-live', 'polite');
toastRegion.setAttribute('aria-atomic', 'true');
document.body.append(toastRegion);
const routes: { id: Route; label: string; hint: string }[] = [
  { id: 'runtime', label: 'Runtime', hint: 'Playback and health' },
  { id: 'media', label: 'Media sources', hint: 'Inputs and capabilities' },
  { id: 'scripts', label: 'Scripts', hint: 'Folders and resources' },
  { id: 'outputs', label: 'Output targets', hint: 'Connections and rates' },
  { id: 'settings', label: 'Device settings', hint: 'Axis range and limits' },
  { id: 'logs', label: 'Logs and health', hint: 'Events and diagnostics' },
];

// The default endpoints follow the address the browser uses to reach this
// server (the fnOS host when deployed), so 127.0.0.1/loopback is replaced by
// the real host IP. Inside the container loopback would never reach host services.
const mfpHost = (typeof window !== 'undefined' && window.location.hostname) || '127.0.0.1';

const mediaEndpointDefaults: Record<string, string> = {
  internal: '',
  emby: `http://${mfpHost}:8096`,
  jellyfin: `http://${mfpHost}:8096`,
  plex: `http://${mfpHost}:32400`,
  vlc: `http://${mfpHost}:8080`,
  mpc: `http://${mfpHost}:13579`,
  ofs: `ws://${mfpHost}:8080/ofs`,
  mpv: `http://${mfpHost}:5000`,
};
const mediaSecretDefaults: Record<string, string> = {
  emby: 'MFP_EMBY_API_KEY',
  jellyfin: 'MFP_JELLYFIN_API_KEY',
  plex: 'MFP_PLEX_TOKEN',
  vlc: 'MFP_VLC_PASSWORD',
};
const outputEndpointDefaults: Record<string, string> = {
  tcp: `${mfpHost}:8080`,
  udp: 'tcode.local:8000',
  websocket: `ws://${mfpHost}/ws`,
  file: 'output.tcode',
  serial: '',
  'the-handy': 'https://www.handyfeeling.com/api/handy/v2/',
  buttplug: `ws://${mfpHost}:12345`,
};

const escapeHtml = (value: string): string => value.replace(/[&<>"']/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[character] ?? character));
const displayAxis = (axis: string | undefined | null): string => (axis && axis.trim()) ? axis.trim() : 'all';
const statusClass = (status: Status | 'pass' | 'warn'): string => status === 'pass' ? 'status-online' : `status-${status}`;
const statusDot = (status: Status | 'pass' | 'warn'): string => `<span class="status-dot ${statusClass(status)}" aria-hidden="true"></span>`;
const axisFriendlyNames: Record<string, string> = {
  L0: 'Up / Down', L1: 'Forward / Backward', L2: 'Left / Right',
  R0: 'Twist', R1: 'Roll', R2: 'Pitch', V0: 'Vibrate', V1: 'Pump',
  A0: 'Valve', A1: 'Suction', A2: 'Lube',
};
const axisTravelUnit = (axis: string): string => axis.startsWith('L') ? 'mm' : axis.startsWith('R') ? '°' : '%';
const percent = (value: number): string => `${Math.round(value * 100)}%`;

function effectiveAxisBounds(axis: DeviceAxisSetting): [number, number] {
  const sourceMinimum = axis.limitEnabled ? axis.limitMinimum : 0;
  const sourceMaximum = axis.limitEnabled ? axis.limitMaximum : 1;
  return [
    axis.minimum + (axis.maximum - axis.minimum) * sourceMinimum,
    axis.minimum + (axis.maximum - axis.minimum) * sourceMaximum,
  ];
}

function axisOrDefault(axes: DeviceAxisSetting[], name: string): DeviceAxisSetting {
  return axes.find((axis) => axis.axis === name) ?? {
    axis: name, minimum: 0, maximum: 1, inverted: false, interpolation: 'Linear',
    limitEnabled: false, limitMinimum: 0, limitMaximum: 1, maximumTravel: null,
  };
}

function applyAxisPreview(axis: DeviceAxisSetting, input: number): number {
  let value = Number.isFinite(input) ? Math.max(0, Math.min(1, input)) : 0.5;
  if (axis.inverted) value = 1 - value;
  if (axis.limitEnabled) value = Math.max(axis.limitMinimum, Math.min(axis.limitMaximum, value));
  return axis.minimum + (axis.maximum - axis.minimum) * value;
}

function axisTravelSummary(axis: DeviceAxisSetting): string {
  const [minimum, maximum] = effectiveAxisBounds(axis);
  const outputSpan = axis.maximum - axis.minimum;
  const effectiveSpan = maximum - minimum;
  const unit = axisTravelUnit(axis.axis);
  if (axis.maximumTravel == null || !Number.isFinite(axis.maximumTravel) || axis.maximumTravel <= 0)
    return `${percent(outputSpan)} output${axis.limitEnabled ? `, ${percent(effectiveSpan)} after safety limit` : ''}; full travel not calibrated`;
  return `${axis.maximumTravel.toFixed(1)} ${unit} full, ${(axis.maximumTravel * outputSpan).toFixed(1)} ${unit} output${axis.limitEnabled ? `, ${(axis.maximumTravel * effectiveSpan).toFixed(1)} ${unit} after limit` : ''}`;
}

function renderPreviewRange(axis: DeviceAxisSetting, group: 'output' | 'limit'): string {
  const minimum = group === 'output' ? axis.minimum : axis.limitMinimum;
  const maximum = group === 'output' ? axis.maximum : axis.limitMaximum;
  const minimumField = group === 'output' ? 'minimum' : 'limitMinimum';
  const maximumField = group === 'output' ? 'maximum' : 'limitMaximum';
  return `<div class="dual-range-control axis-preview-range${group === 'limit' && !axis.limitEnabled ? ' is-disabled' : ''}" data-axis-preview-range="${group}" style="--range-start:${minimum * 100}%;--range-end:${maximum * 100}%"><div class="dual-range-track"><span></span></div><input class="dual-range-input range-lower" data-axis-preview-field="${minimumField}" aria-label="Preview ${escapeHtml(axis.axis)} ${group === 'output' ? 'output minimum' : 'limit minimum'}" type="range" min="0" max="1" step="0.01" value="${minimum}" /><input class="dual-range-input range-upper" data-axis-preview-field="${maximumField}" aria-label="Preview ${escapeHtml(axis.axis)} ${group === 'output' ? 'output maximum' : 'limit maximum'}" type="range" min="0" max="1" step="0.01" value="${maximum}" /><div class="dual-range-values"><output data-axis-preview-range-value="minimum">${percent(minimum)}</output><output data-axis-preview-range-value="maximum">${percent(maximum)}</output></div></div>`;
}

function renderDeviceAxisVisualization(axes: DeviceAxisSetting[], state?: AppState): string {
  const axis = axisOrDefault(axes, selectedAxisPreview);
  const targets = state?.outputs ?? [];
  if (selectedAxisTargetId === null || !targets.some((t) => t.id === selectedAxisTargetId)) {
    selectedAxisTargetId = targets.find((t) => t.status === 'online')?.id ?? targets[0]?.id ?? null;
  }
  const activeTarget = targets.find((t) => t.id === selectedAxisTargetId);
  const isOnline = activeTarget?.status === 'online';

  const targetSelectorHtml = targets.length === 0
    ? `<div class="axis-preview-target-empty"><span>No output targets configured.</span><button type="button" class="button button-secondary" data-route="outputs">Configure targets</button></div>`
    : `<div class="axis-preview-target-row">
        <select class="axis-preview-target-select" data-axis-target-select aria-label="Select output target for hardware sync">
          ${targets.map((t) => `<option value="${escapeHtml(t.id)}"${t.id === selectedAxisTargetId ? ' selected' : ''}>${escapeHtml(t.name)} (${escapeHtml(t.protocol)}) · ${statusLabel(t.status)}</option>`).join('')}
        </select>
        <button type="button" class="button ${isOnline ? 'button-danger' : 'button-primary'}" data-axis-target-toggle="${escapeHtml(activeTarget?.id ?? '')}" ${activeTarget?.status === 'busy' ? 'disabled' : ''}>
          <span class="status-dot ${statusClass(activeTarget?.status ?? 'offline')}" aria-hidden="true"></span>
          <span>${isOnline ? 'Disconnect' : activeTarget?.status === 'busy' ? 'Connecting...' : 'Connect target'}</span>
        </button>
      </div>`;

  const syncControlHtml = `
    <div class="axis-preview-target-bar">
      <div class="axis-preview-target-header">
        <div>
          <strong>Real device target</strong>
          <span data-axis-target-status-label>${activeTarget ? `${escapeHtml(activeTarget.name)} · ${escapeHtml(activeTarget.endpoint)}` : 'Connect an output target to sync live movements'}</span>
        </div>
        <span class="status-badge ${statusClass(activeTarget?.status ?? 'offline')}" data-axis-target-badge>${statusDot(activeTarget?.status ?? 'offline')}<span data-axis-target-status-text>${statusLabel(activeTarget?.status ?? 'offline')}</span></span>
      </div>
      ${targetSelectorHtml}
      <div class="axis-preview-target-sync-row">
        <label class="axis-checkbox" title="Mirror real device movement during playback onto 3D model">
          <input type="checkbox" data-axis-target-sync${followLiveTarget ? ' checked' : ''} />
          <span>Follow live device movements</span>
        </label>
        <span data-axis-target-live-hint>${isOnline ? (followLiveTarget ? 'Live mirroring active' : 'Mirroring paused') : 'Device offline'}</span>
      </div>
    </div>`;

  return `<div class="axis-model-layout"><div class="axis-model-stage" data-axis-model-host data-model-state="disabled" role="group" aria-label="Interactive SR6 axis movement preview"><div class="axis-model-status"><button type="button" class="button button-secondary" data-axis-model-enable>Enable 3D preview</button></div></div><div class="axis-model-controls">${syncControlHtml}<div class="axis-model-tabs" role="tablist" aria-label="Axis to preview">${modeledAxisNames.map((name) => `<button class="axis-model-tab${name === selectedAxisPreview ? ' is-active' : ''}" type="button" role="tab" aria-selected="${name === selectedAxisPreview}" data-axis-preview-select="${name}"><strong>${name}</strong><span>${escapeHtml(axisFriendlyNames[name])}</span></button>`).join('')}</div><div class="axis-preview-simulation"><div><strong>Simulation</strong><span data-axis-preview-simulation-status>6 axes</span></div><button class="button button-primary" type="button" data-axis-preview-simulate>Simulate all axes</button></div><div class="axis-model-selected"><div><p class="eyebrow">Selected movement</p><h3><span data-axis-preview-name>${axis.axis}</span> <small data-axis-preview-friendly>${escapeHtml(axisFriendlyNames[axis.axis])}</small></h3></div><output data-axis-preview-output>${percent(applyAxisPreview(axis, axisPreviewPositions[selectedAxisPreview]))}</output></div><label class="axis-preview-position"><span>Input position <output data-axis-preview-input-value>${percent(axisPreviewPositions[selectedAxisPreview])}</output></span><input data-axis-preview-position aria-label="${axis.axis} preview input position" type="range" min="0" max="1" step="0.01" value="${axisPreviewPositions[selectedAxisPreview]}" /></label><div class="axis-wizard-banner"><div><strong>Axis calibration wizard</strong><span>Teach physical min/max step-by-step with live 3D & hardware sync.</span></div><button type="button" class="button button-primary" data-axis-wizard-open>📐 Guided calibration</button></div><div class="axis-preview-presets"><button class="button button-secondary" type="button" data-axis-preview-preset="0">Minimum</button><button class="button button-secondary" type="button" data-axis-preview-preset="0.5">Center</button><button class="button button-secondary" type="button" data-axis-preview-preset="1">Maximum</button><button class="button button-secondary" type="button" data-axis-preview-sweep>Sweep axis</button></div><div class="axis-preview-setting"><div class="axis-preview-setting-heading"><strong>Output range</strong><span data-axis-preview-output-summary>${percent(axis.minimum)} to ${percent(axis.maximum)}</span></div>${renderPreviewRange(axis, 'output')}</div><div class="axis-preview-setting"><div class="axis-preview-setting-heading"><label class="axis-checkbox"><input data-axis-preview-field="limitEnabled" aria-label="Preview ${axis.axis} limit enabled" type="checkbox"${axis.limitEnabled ? ' checked' : ''} /> Safety limit</label><span data-axis-preview-limit-summary>${axis.limitEnabled ? `${percent(axis.limitMinimum)} to ${percent(axis.limitMaximum)}` : 'Disabled'}</span></div>${renderPreviewRange(axis, 'limit')}</div><label class="axis-preview-reverse axis-checkbox"><input data-axis-preview-field="inverted" aria-label="Preview ${axis.axis} inverted" type="checkbox"${axis.inverted ? ' checked' : ''} /> Reverse this axis</label><p class="axis-preview-travel" data-axis-travel-summary="${axis.axis}"></p></div></div>`;
}

function renderDualRange(axis: DeviceAxisSetting, group: 'output' | 'limit', minimum: number, maximum: number): string {
  const minimumField = group === 'output' ? 'minimum' : 'limitMinimum';
  const maximumField = group === 'output' ? 'maximum' : 'limitMaximum';
  return `<div class="dual-range-control${group === 'limit' && !axis.limitEnabled ? ' is-disabled' : ''}" data-axis-range="${group}" style="--range-start:${minimum * 100}%;--range-end:${maximum * 100}%"><div class="dual-range-track"><span></span></div><input class="dual-range-input range-lower" data-axis-field="${minimumField}" aria-label="${escapeHtml(axis.axis)} ${group === 'output' ? 'minimum' : 'limit minimum'}" type="range" min="0" max="1" step="0.01" value="${minimum}" /><input class="dual-range-input range-upper" data-axis-field="${maximumField}" aria-label="${escapeHtml(axis.axis)} ${group === 'output' ? 'maximum' : 'limit maximum'}" type="range" min="0" max="1" step="0.01" value="${maximum}" /><div class="dual-range-values"><output data-range-value="minimum">${percent(minimum)}</output><output data-range-value="maximum">${percent(maximum)}</output></div></div>`;
}

function renderAuthScreen(message = '') {
  stopAxisPreviewAnimations();
  destroyOsrAxisPreview();
  renderedRoute = null;
  appRoot.innerHTML = `<main class="auth-shell"><section class="auth-panel panel" aria-labelledby="auth-title"><div class="brand-lockup"><div class="brand-mark" aria-hidden="true">MFP</div><div><strong>MultiFunPlayer</strong><span>Web access</span></div></div><p class="eyebrow">Protected server</p><h1 id="auth-title">Connect to your control room</h1><p class="page-description">Use an existing access token, or complete the one-time pairing flow.</p>${message ? `<p class="auth-error" role="alert">${escapeHtml(message)}</p>` : ''}<form data-auth-form="server" class="auth-form"><label for="auth-access-token">Access token<input id="auth-access-token" name="accessToken" type="password" autocomplete="current-password" placeholder="Bearer token" /></label><div class="auth-divider"><span>or pair this browser</span></div><label for="auth-pairing-token">Pairing token<input id="auth-pairing-token" name="pairingToken" type="password" autocomplete="one-time-code" placeholder="One-time pairing token" /></label><label for="auth-new-token">New access token<input id="auth-new-token" name="newAuthToken" type="password" autocomplete="new-password" minlength="16" placeholder="At least 16 characters" /></label><div class="auth-actions"><button class="button button-primary" type="submit" data-auth-mode="connect">Connect</button><button class="button button-secondary" type="submit" data-auth-mode="pair">Pair and connect</button></div></form></section></main>`;
}

async function connectRemoteStore(showError = false): Promise<boolean> {
  try {
    const remoteStore = createRemoteStore(apiClient);
    try { await remoteStore.connect(); } catch (error) { remoteStore.close(); throw error; }
    unsubscribeStore();
    store.close();
    store = remoteStore;
    unsubscribeStore = store.subscribe(onStoreChanged);
    if (routes.some((route) => route.id === initialRoute)) store.setRoute(initialRoute);
    return true;
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      apiClient.clearToken();
      unsubscribeStore();
      renderAuthScreen('The access token was rejected.');
      return false;
    }
    if (showError) {
      unsubscribeStore();
      renderAuthScreen('The server could not be reached. Check the Web entry and try again.');
    }
    return false;
  }
}

function renderShell() {
  appRoot.innerHTML = `
    <div class="app-shell">
      <aside class="sidebar" aria-label="Primary navigation">
        <div class="brand-lockup">
          <div class="brand-mark" aria-hidden="true">MFP</div>
          <div><strong>MultiFunPlayer</strong><span>Control room</span></div>
        </div>
        <div class="demo-chip"><span class="live-dot" aria-hidden="true"></span><span data-bind="connection-mode"></span></div>
        <nav class="nav-list">
          ${routes.map((route) => `<button class="nav-item" data-route="${route.id}" aria-current="false"><span class="nav-label">${route.label}</span><span class="nav-hint">${route.hint}</span></button>`).join('')}
        </nav>
        <div class="sidebar-footer">
          <div class="version-line"><span>Web prototype</span><span>v0.1</span></div>
        </div>
      </aside>
      <div class="workspace">
        <header class="topbar">
          <div class="breadcrumbs"><span>Workspace</span><span aria-hidden="true">/</span><strong data-bind="route-label"></strong></div>
          <div class="topbar-actions">
            <span class="sync-state">${statusDot('pass')}<span data-bind="last-sync"></span></span>
            <button class="icon-button" data-action="refresh" aria-label="Reload workspace" title="Reload workspace">Reload</button>
            <button class="avatar" aria-label="Open account menu">MP</button>
          </div>
        </header>
        <main class="main-content" tabindex="-1"></main>
      </div>
    </div>
  `;
}

function prepareRouteControls(main: HTMLElement) {
  main.querySelectorAll<HTMLInputElement | HTMLSelectElement>('.resource-form label > input, .resource-form label > select').forEach((control) => {
    const labelText = Array.from(control.parentElement?.childNodes ?? [])
      .find((node) => node.nodeType === Node.TEXT_NODE)?.textContent?.trim();
    if (labelText && !control.hasAttribute('aria-label')) control.setAttribute('aria-label', labelText);
  });
  main.querySelectorAll<HTMLInputElement>('.resource-form input[pattern]').forEach((input) => {
    const pattern = input.getAttribute('pattern');
    // Chromium's current HTML pattern grammar uses the Unicode `v` flag,
    // where a literal hyphen in a character class must be escaped.
    if (pattern?.includes('_-')) input.setAttribute('pattern', pattern.replace('_-', '_\\x2d'));
  });
  const mediaForm = main.querySelector<HTMLFormElement>('[data-resource-form="media"]');
  if (mediaForm) synchronizeMediaForm(mediaForm, true);
  const outputForm = main.querySelector<HTMLFormElement>('[data-resource-form="output"]');
  if (outputForm) synchronizeOutputForm(outputForm, true);
  const axisForm = main.querySelector<HTMLFormElement>('[data-resource-form="axis"]');
  const axisModelHost = main.querySelector<HTMLElement>('[data-axis-model-host]');
  if (axisForm && axisModelHost) updateAxisVisualization(axisForm);
  if (main.querySelector('[data-runtime-router]'))
    window.requestAnimationFrame(() => drawRuntimeWires());
}

function updateChrome(state: AppState) {
  const setText = (selector: string, value: string) => {
    const element = appRoot.querySelector<HTMLElement>(selector);
    if (element && element.textContent !== value) element.textContent = value;
  };
  setText('[data-bind="connection-mode"]', state.mode === 'server' ? 'Server API connected' : 'Local demo mode');
  setText('[data-bind="route-label"]', routes.find((item) => item.id === state.route)?.label ?? 'Workspace');
  setText('[data-bind="last-sync"]', `Synced ${state.lastSync}`);
  appRoot.querySelectorAll<HTMLButtonElement>('.nav-item[data-route]').forEach((button) => {
    const active = button.dataset.route === state.route;
    button.classList.toggle('active', active);
    button.setAttribute('aria-current', active ? 'page' : 'false');
  });
}

function render(state: AppState, change: StoreChange = 'full') {
  if (!appRoot.querySelector('.app-shell')) {
    renderShell();
    renderedRoute = null;
  }
  updateChrome(state);
  const main = appRoot.querySelector<HTMLElement>('.main-content');
  if (!main) return;
  if (change === 'status' && renderedRoute === state.route) {
    updateStatusBindings(state);
    return;
  }
  stopAxisPreviewAnimations();
  destroyOsrAxisPreview();
  main.innerHTML = renderRoute(state);
  renderedRoute = state.route;
  prepareRouteControls(main);
}

function pageHeading(eyebrow: string, title: string, description: string, actions = ''): string {
  return `<div class="page-heading"><div><p class="eyebrow">${eyebrow}</p><h1>${title}</h1><p class="page-description">${description}</p></div>${actions ? `<div class="heading-actions">${actions}</div>` : ''}</div>`;
}

function renderRoute(state: AppState): string {
  switch (state.route) {
    case 'media': return renderMedia(state);
    case 'scripts': return renderScripts(state);
    case 'outputs': return renderOutputs(state);
    case 'settings': return renderDeviceSettings(state);
    case 'logs': return renderLogs(state);
    case 'runtime': return renderRuntime(state);
  }
}

function renderRuntime(state: AppState): string {
  const onlineOutputs = state.outputs.filter((output) => output.status === 'online').length;
  return `
    ${pageHeading('Overview', 'Runtime dashboard', 'Route media clocks to device outputs and control every active session.')}
    ${renderRuntimeRouter(state)}
    ${renderRoutedPlayback(state)}
    <section class="metric-grid" aria-label="Runtime metrics">
      ${metricCard('Media source', `${state.media.filter((source) => source.status === 'online').length} / ${state.media.length}`, 'sources connected', 'online')}
      ${metricCard('Output targets', `${onlineOutputs} / ${state.outputs.length}`, 'targets streaming', onlineOutputs === state.outputs.length ? 'online' : 'warning')}
      ${metricCard('Loop latency', '1.2 ms', 'median over 60 seconds', 'online')}
      ${metricCard('Events', `${state.logs.length}`, 'recent runtime entries', 'neutral')}
    </section>
    <div class="content-grid two-thirds">
      <section class="panel output-panel" aria-labelledby="output-health-title">
        <div class="panel-heading"><div><p class="eyebrow">Live connections</p><h2 id="output-health-title">Output health</h2></div><button class="text-button" data-route="outputs">View all</button></div>
        <div class="output-list">${state.outputs.map(renderOutputRow).join('')}</div>
      </section>
      <section class="panel" aria-labelledby="health-title">
        <div class="panel-heading"><div><p class="eyebrow">System checks</p><h2 id="health-title">Health</h2></div><span class="health-score" data-bind="health-score">${state.health.filter((check) => check.status === 'pass').length}/${state.health.length}</span></div>
        <div class="health-list">${renderHealthItems(state)}</div>
      </section>
    </div>
    <section class="panel activity-panel" aria-labelledby="activity-title">
      <div class="panel-heading"><div><p class="eyebrow">Latest events</p><h2 id="activity-title">Activity</h2></div><button class="text-button" data-route="logs">Open logs</button></div>
      ${renderLogTable(state.logs.slice(0, 4))}
    </section>`;
}

function outputAssignment(state: AppState, outputId: string): string | null {
  return state.runtimeRoutes.find((route) => route.outputIds.includes(outputId))?.sourceId ?? null;
}

function renderRuntimeRouter(state: AppState): string {
  const connectionCount = state.runtimeRoutes.reduce((count, route) => count + route.outputIds.length, 0);
  const sourceRows = state.media.length === 0 ? '<div class="router-empty">No sources</div>' : state.media.map((source) => {
    const routedCount = state.runtimeRoutes.find((route) => route.sourceId === source.id)?.outputIds.length ?? 0;
    return `<div class="router-node router-source" data-router-source="${escapeHtml(source.id)}"><div class="router-node-copy"><span class="status-dot ${statusClass(source.status)}" aria-hidden="true"></span><div><strong>${escapeHtml(source.name)}</strong><span>${escapeHtml(source.kind)} · ${routedCount} routed</span></div></div><button class="router-port router-port-source" type="button" data-route-source-port="${escapeHtml(source.id)}" aria-label="Route ${escapeHtml(source.name)}" title="Drag to an output"></button></div>`;
  }).join('');
  const sourceOptions = state.media.map((source) => `<option value="${escapeHtml(source.id)}">${escapeHtml(source.name)}</option>`).join('');
  const outputRows = state.outputs.length === 0 ? '<div class="router-empty">No outputs</div>' : state.outputs.map((output) => {
    const assignedSourceId = outputAssignment(state, output.id);
    const assignedSource = state.media.find((source) => source.id === assignedSourceId);
    return `<div class="router-node router-output ${assignedSource ? 'is-routed' : ''}" data-router-output="${escapeHtml(output.id)}"><button class="router-port router-port-output" type="button" data-route-output-port="${escapeHtml(output.id)}" aria-label="Connect ${escapeHtml(output.name)}" title="Drop a source here"></button><div class="router-node-copy"><span class="status-dot ${statusClass(output.status)}" aria-hidden="true"></span><div><strong>${escapeHtml(output.name)}</strong><span>${escapeHtml(output.protocol)} · ${assignedSource ? escapeHtml(assignedSource.name) : 'Unassigned'}</span></div></div>${assignedSource ? `<button class="router-disconnect" type="button" data-runtime-unroute="${escapeHtml(output.id)}" aria-label="Disconnect ${escapeHtml(output.name)}" title="Remove route">&times;</button>` : ''}<label class="router-assignment"><span class="sr-only">Source for ${escapeHtml(output.name)}</span><select data-runtime-output-assignment="${escapeHtml(output.id)}"><option value="">Unassigned</option>${sourceOptions.replace(`value="${escapeHtml(assignedSourceId ?? '')}"`, `value="${escapeHtml(assignedSourceId ?? '')}" selected`)}</select></label></div>`;
  }).join('');
  return `<section class="runtime-routing" aria-labelledby="runtime-routing-title"><div class="panel-heading"><div><p class="eyebrow">Signal routing</p><h2 id="runtime-routing-title">Source to output</h2></div><span class="result-count">${connectionCount} connections</span></div><div class="runtime-router panel" data-runtime-router><svg class="router-wires" data-router-wires aria-hidden="true"></svg><div class="router-column"><div class="router-column-title"><span>Sources</span><small>${state.media.length}</small></div><div class="router-list">${sourceRows}</div></div><div class="router-column router-column-output"><div class="router-column-title"><span>Outputs</span><small>${state.outputs.length}</small></div><div class="router-list">${outputRows}</div></div></div></section>`;
}

function renderRoutedPlayback(state: AppState): string {
  const routed = state.runtimeRoutes.map((route) => ({
    route,
    source: state.media.find((source) => source.id === route.sourceId),
    outputs: route.outputIds.map((id) => state.outputs.find((output) => output.id === id)).filter(Boolean),
  })).filter((item) => item.source);
  const controls = routed.map(({ route, source, outputs }) => {
    if (!source) return '';
    const media = source.mediaState;
    const duration = Math.max(0, media?.durationSeconds ?? 0);
    const position = Math.min(duration || Number.MAX_SAFE_INTEGER, Math.max(0, media?.positionSeconds ?? 0));
    const available = source.status === 'online' && media !== undefined;
    const outputBadges = outputs.map((output) => output ? `<span>${escapeHtml(output.name)}</span>` : '').join('');
    const matchMode = route.scriptMatchMode === 'global' ? 'exact' : route.scriptMatchMode;
    const emptyScriptLabel = matchMode === 'exact' ? 'No exact match'
      : matchMode === 'fuzzy' ? 'No fuzzy match' : 'No script selected';
    const scriptOptions = state.scripts.map((script) => `<option value="${escapeHtml(script.id)}" ${route.scriptId === script.id ? 'selected' : ''}>${escapeHtml(script.name)}</option>`).join('');
    const matchButtons = (['exact', 'fuzzy', 'manual'] as ScriptMatchMode[]).map((mode) => `<button type="button" class="script-match-button ${matchMode === mode ? 'is-active' : ''}" data-runtime-script-match="${escapeHtml(source.id)}" data-script-match-mode="${mode}">${mode[0].toUpperCase()}${mode.slice(1)}</button>`).join('');
    const speedOptions = [0.5, 0.75, 1, 1.25, 1.5, 2].map((speed) => `<option value="${speed}" ${Math.abs((media?.playbackSpeed ?? 1) - speed) < 0.001 ? 'selected' : ''}>${speed}x</option>`).join('');
    return `<article class="routed-playback panel" data-runtime-playback="${escapeHtml(source.id)}"><div class="routed-playback-heading"><div><div class="section-kicker"><span class="status-dot ${statusClass(source.status)}" data-runtime-source-status aria-hidden="true"></span>${escapeHtml(source.kind)}</div><h2>${escapeHtml(source.name)}</h2><p data-runtime-media-path>${escapeHtml(media?.path ?? source.endpoint)}</p></div><div class="routed-output-badges">${outputBadges}</div></div><div class="routed-script-binding"><div class="routed-script-binding-heading"><span>Script</span><div class="script-match-modes" role="group" aria-label="${escapeHtml(source.name)} script matching mode">${matchButtons}</div></div><div class="routed-script-fields"><label><span class="sr-only">Search scripts for ${escapeHtml(source.name)}</span><input type="search" data-runtime-script-search="${escapeHtml(source.id)}" placeholder="Search scripts" autocomplete="off" /></label><label><span class="sr-only">Script for ${escapeHtml(source.name)}</span><select data-runtime-source-script="${escapeHtml(source.id)}"><option value="">${escapeHtml(emptyScriptLabel)}</option>${scriptOptions}</select></label></div></div><div class="routed-progress"><input type="range" min="0" max="${Math.max(0.1, duration)}" step="0.1" value="${position}" data-runtime-source-seek="${escapeHtml(source.id)}" aria-label="${escapeHtml(source.name)} playback position" ${!available || !source.controls.canSeek ? 'disabled' : ''}/><div><span data-runtime-source-position>${formatTime(position)}</span><span data-runtime-source-duration>${duration > 0 ? formatTime(duration) : '--:--'}</span></div></div><div class="routed-controls"><button class="button button-primary" type="button" data-runtime-source-toggle="${escapeHtml(source.id)}" ${!available || !source.controls.canPlayPause ? 'disabled' : ''}>${media?.playing ? 'Pause' : 'Play'}</button><label class="compact-select"><span>Speed</span><select data-runtime-source-speed="${escapeHtml(source.id)}" ${!available || !source.controls.canSetPlaybackSpeed ? 'disabled' : ''}>${speedOptions}</select></label><label class="toggle-option"><input type="checkbox" data-runtime-source-loop="${escapeHtml(source.id)}" ${route.loopEnabled ? 'checked' : ''} ${!source.controls.canLoop ? 'disabled' : ''}/><span>Loop</span></label></div></article>`;
  }).join('');
  return `<section class="routed-playback-section" aria-labelledby="routed-playback-title"><div class="panel-heading"><div><p class="eyebrow">Playing</p><h2 id="routed-playback-title">Routed sessions</h2></div><span class="result-count">${routed.length} active sources</span></div><div class="routed-playback-list">${controls || '<div class="empty-state panel">No routed sources.</div>'}</div></section>`;
}

function routerPath(startX: number, startY: number, endX: number, endY: number): string {
  const bend = Math.max(48, Math.abs(endX - startX) * 0.45);
  return `M ${startX} ${startY} C ${startX + bend} ${startY}, ${endX - bend} ${endY}, ${endX} ${endY}`;
}

function drawRuntimeWires(pointer?: { x: number; y: number }) {
  const router = document.querySelector<HTMLElement>('[data-runtime-router]');
  const svg = router?.querySelector<SVGSVGElement>('[data-router-wires]');
  if (!router || !svg) return;
  const bounds = router.getBoundingClientRect();
  svg.setAttribute('viewBox', `0 0 ${bounds.width} ${bounds.height}`);
  const center = (element: Element) => {
    const rect = element.getBoundingClientRect();
    return { x: rect.left - bounds.left + rect.width / 2, y: rect.top - bounds.top + rect.height / 2 };
  };
  const paths: string[] = [];
  store.getState().runtimeRoutes.forEach((route, routeIndex) => {
    const source = router.querySelector<HTMLElement>(`[data-route-source-port="${route.sourceId}"]`);
    if (!source) return;
    const start = center(source);
    route.outputIds.forEach((outputId) => {
      const output = router.querySelector<HTMLElement>(`[data-route-output-port="${outputId}"]`);
      if (!output) return;
      const end = center(output);
      paths.push(`<path class="router-wire" style="--wire-index:${routeIndex}" d="${routerPath(start.x, start.y, end.x, end.y)}" data-runtime-unroute="${escapeHtml(outputId)}"/>`);
    });
  });
  if (routingDrag && pointer) {
    const source = router.querySelector<HTMLElement>(`[data-route-source-port="${routingDrag.sourceId}"]`);
    if (source) {
      const start = center(source);
      paths.push(`<path class="router-wire router-wire-preview" d="${routerPath(start.x, start.y, pointer.x - bounds.left, pointer.y - bounds.top)}"/>`);
    }
  }
  svg.innerHTML = paths.join('');
}

async function setRuntimeOutputAssignment(outputId: string, sourceId: string | null) {
  const routes = store.getState().runtimeRoutes.map((route): RuntimeSourceRoute => ({
    ...route,
    outputIds: route.outputIds.filter((id) => id !== outputId),
  })).filter((route) => route.outputIds.length > 0);
  if (sourceId) {
    const existing = routes.find((route) => route.sourceId === sourceId);
    if (existing) existing.outputIds.push(outputId);
    else routes.push({ sourceId, outputIds: [outputId], loopEnabled: false, scriptId: null, scriptMatchMode: 'exact' });
  }
  await store.setRuntimeRoutes(routes);
}

function metricCard(label: string, value: string, detail: string, status: string): string {
  return `<article class="metric-card"><div class="metric-label">${label}${status === 'neutral' ? '' : statusDot(status as Status)}</div><strong>${value}</strong><span>${detail}</span></article>`;
}

function renderHealthItems(state: AppState): string {
  return state.health.map((check, index) => `<div class="health-item" data-health-index="${index}">${statusDot(check.status)}<div><strong data-health-label>${check.label}</strong><span data-health-detail>${check.detail}</span></div></div>`).join('');
}

function renderOutputRow(output: AppState['outputs'][number]): string {
  return `<div class="output-row"><div class="output-name"><span class="status-icon ${statusClass(output.status)}">${output.protocol.slice(0, 1)}</span><div><strong>${escapeHtml(output.name)}</strong><span>${escapeHtml(output.endpoint)} · ${escapeHtml(displayAxis(output.sourceAxis))}</span></div></div><div class="output-row-metrics"><span>${output.sentFrames.toLocaleString()} sent</span><span>${output.coalescedFrames.toLocaleString()} coalesced</span></div><div class="output-row-status">${statusDot(output.status)}<span>${statusLabel(output.status)}</span></div><button class="more-button" data-output-toggle="${output.id}" aria-label="Toggle ${escapeHtml(output.name)}">${output.status === 'online' ? 'Stop' : 'Start'}</button></div>`;
}

function renderMedia(state: AppState): string {
  return `${pageHeading('Inputs', 'Media sources', 'Connect local folders and live relays. Capabilities are advertised before a source is enabled.', '<button class="button button-primary" data-action="focus-media-form">Add source</button>')}
    <div class="notice notice-info"><strong>Server-managed resources.</strong><span>Changes are persisted by the API; unsupported integrations remain visible with their implementation status.</span></div>
    ${renderMediaForm()}
    <section class="source-grid" aria-label="Media source list">${state.media.length === 0 ? '<div class="empty-state panel">No media sources configured.</div>' : state.media.map(renderMediaSourceCard).join('')}</section>`;
}

function renderMediaSourceCard(source: AppState['media'][number]): string {
  const mediaState = source.mediaState;
  const controls = mediaState && source.status === 'online' ? `<div class="media-controls" aria-label="Remote playback controls">
    <div class="media-position"><span>${mediaState.playing ? 'Playing' : 'Paused'}</span><span>${formatTime(mediaState.positionSeconds)}${mediaState.durationSeconds > 0 ? ` / ${formatTime(mediaState.durationSeconds)}` : ''}</span></div>
    <div class="resource-actions"><button class="button button-secondary" data-media-play="${escapeHtml(source.id)}">Play</button><button class="button button-secondary" data-media-pause="${escapeHtml(source.id)}">Pause</button><input class="media-seek-input" data-media-seek-value="${escapeHtml(source.id)}" type="number" min="0" step="0.1" value="${Math.max(0, mediaState.positionSeconds).toFixed(1)}" aria-label="Seek position in seconds" /><button class="button button-secondary" data-media-seek="${escapeHtml(source.id)}">Seek</button></div>
  </div>` : '';
  return `<article class="source-card panel"><div class="card-topline"><span class="protocol-label">${escapeHtml(source.kind)}</span><span class="status-badge ${statusClass(source.status)}">${statusDot(source.status)}${statusLabel(source.status)}</span></div><h2>${escapeHtml(source.name)}</h2><p class="muted">${escapeHtml(source.endpoint)}</p><div class="capability-list">${source.capabilities.map((capability) => `<span>${escapeHtml(capability)}</span>`).join('')}</div>${source.error ? `<p class="resource-error">${escapeHtml(source.error)}</p>` : ''}${controls}<div class="source-footer"><span>${source.latency ? `${source.latency} ms latency` : 'No heartbeat'}</span><div class="resource-actions"><button class="button button-secondary" data-media-retest="${escapeHtml(source.id)}">Test</button><button class="button button-secondary" data-media-toggle="${escapeHtml(source.id)}">${source.status === 'online' ? 'Disconnect' : 'Connect'}</button><button class="button button-secondary" data-media-edit="${escapeHtml(source.id)}">Edit</button><button class="button button-danger" data-media-delete="${escapeHtml(source.id)}">Delete</button></div></div></article>`;
}

function renderMediaForm(): string {
  return `<form class="resource-form panel" data-resource-form="media"><input type="hidden" name="id" /><div class="panel-heading"><div><p class="eyebrow">New input</p><h2>Register media source</h2></div><span class="result-count">Persisted on server</span></div><div class="resource-form-grid"><label for="media-name">Name <span class="optional-marker" aria-hidden="true">Optional</span><input id="media-name" name="name" placeholder="Generated from kind" /></label><label for="media-kind">Kind<select id="media-kind" name="kind"><option value="internal">Internal</option><option value="emby">Emby</option><option value="jellyfin">Jellyfin</option><option value="plex">Plex</option><option value="vlc">VLC</option><option value="mpc">MPC-HC/BE</option><option value="ofs">OFS</option><option value="mpv">mpv proxy</option></select></label><label for="media-endpoint" data-media-field="endpoint">Endpoint<input id="media-endpoint" name="endpoint" placeholder="Select a kind to fill its default" /></label><label for="media-device-id" data-media-field="device">Remote device id <span class="optional-marker" aria-hidden="true">Optional</span><input id="media-device-id" name="deviceId" maxlength="256" placeholder="Session or playback client id" /></label><label for="media-secret" data-media-field="secret">API key / secret <span class="optional-marker" aria-hidden="true">Optional</span><input id="media-secret" name="secretValue" type="password" autocomplete="off" placeholder="Paste the API key or token" /></label></div><div class="form-assist"><button class="button button-secondary" type="button" data-action="discover-media">Scan local network</button><button class="button button-secondary" type="button" data-action="test-media-draft">Test connection</button><a class="form-help-link" data-media-key-help target="_blank" rel="noopener noreferrer" hidden>Get API key</a></div><div class="discovery-results" data-discovery-results="media" aria-live="polite" hidden></div><p class="form-hint">The API key or token is stored on the server with this media source. Leave empty to fall back to the host environment variable.</p><div class="resource-form-options"><label><input type="checkbox" name="enabled" checked /> Enabled</label><label><input type="checkbox" name="autoConnect" checked /> Connect on startup</label><button class="button button-primary" type="submit">Create source</button><button class="button button-secondary" type="button" data-resource-cancel="media">Cancel</button></div></form>`;
}

function scriptFolderOf(id: string): string {
  const index = id.lastIndexOf('/');
  return index > 0 ? id.slice(0, index) : '';
}

interface ScriptTreeFolder {
  name: string;
  path: string;
  folders: Map<string, ScriptTreeFolder>;
  scripts: ScriptResource[];
}

function normalizeScriptTreePath(path: string): string {
  return path.replaceAll('\\', '/').split('/').filter((part) => part && part !== '.' && part !== '..').join('/');
}

function ensureScriptTreeFolder(root: ScriptTreeFolder, path: string): ScriptTreeFolder {
  let folder = root;
  let currentPath = '';
  for (const name of normalizeScriptTreePath(path).split('/').filter(Boolean)) {
    currentPath = currentPath ? `${currentPath}/${name}` : name;
    let child = folder.folders.get(name);
    if (!child) {
      child = { name, path: currentPath, folders: new Map(), scripts: [] };
      folder.folders.set(name, child);
    }
    folder = child;
  }
  return folder;
}

function buildScriptTree(scripts: ScriptResource[], paths: string[]): ScriptTreeFolder {
  const root: ScriptTreeFolder = { name: 'Script library', path: '', folders: new Map(), scripts: [] };
  paths.forEach((path) => ensureScriptTreeFolder(root, path));
  scripts.forEach((script) => ensureScriptTreeFolder(root, scriptFolderOf(normalizeScriptTreePath(script.id))).scripts.push(script));
  return root;
}

function scriptTreeFileCount(folder: ScriptTreeFolder): number {
  return folder.scripts.length + [...folder.folders.values()].reduce((total, child) => total + scriptTreeFileCount(child), 0);
}

function scriptTreeFolderCount(folder: ScriptTreeFolder): number {
  return folder.folders.size + [...folder.folders.values()].reduce((total, child) => total + scriptTreeFolderCount(child), 0);
}

function scriptExtension(script: ScriptResource): string {
  const fileName = normalizeScriptTreePath(script.id).split('/').pop() ?? '';
  const index = fileName.lastIndexOf('.');
  return index > 0 ? fileName.slice(index).toLowerCase() : '';
}

function findScriptTreeFolder(root: ScriptTreeFolder, path: string): ScriptTreeFolder | null {
  let folder = root;
  for (const name of normalizeScriptTreePath(path).split('/').filter(Boolean)) {
    const child = folder.folders.get(name);
    if (!child) return null;
    folder = child;
  }
  return folder;
}

function resolveSelectedScriptFolder(root: ScriptTreeFolder): string {
  if (selectedScriptFolderPath === null) selectedScriptFolderPath = '';
  if (!findScriptTreeFolder(root, selectedScriptFolderPath)) selectedScriptFolderPath = '';
  return selectedScriptFolderPath;
}

function renderScriptTreeFolder(folder: ScriptTreeFolder, selectedFolder: string): string {
  const containsSelected = selectedFolder === folder.path || selectedFolder.startsWith(`${folder.path}/`);
  const folders = [...folder.folders.values()]
    .sort((left, right) => left.name.localeCompare(right.name))
    .map((child) => renderScriptTreeFolder(child, selectedFolder))
    .join('');
  const children = folders || '<span class="script-tree-empty">No subfolders</span>';
  const fileCount = scriptTreeFileCount(folder);
  const selected = selectedFolder === folder.path;
  return `<details class="script-tree-folder" data-script-tree-folder="${escapeHtml(folder.path)}"${containsSelected ? ' open' : ''}><summary class="${selected ? 'selected' : ''}" data-select-script-folder="${escapeHtml(folder.path)}"><span class="script-tree-caret" aria-hidden="true"></span><span class="script-tree-folder-icon" aria-hidden="true"></span><strong>${escapeHtml(folder.name)}</strong><button class="script-tree-delete" type="button" data-delete-script-folder="${escapeHtml(folder.path)}" title="Delete folder" aria-label="Delete ${escapeHtml(folder.name)}">&#10005;</button><small>${fileCount} ${fileCount === 1 ? 'file' : 'files'}</small></summary><div class="script-tree-children">${children}</div></details>`;
}

function renderScriptTree(root: ScriptTreeFolder, selectedFolder: string): string {
  const folders = [...root.folders.values()]
    .sort((left, right) => left.name.localeCompare(right.name))
    .map((folder) => renderScriptTreeFolder(folder, selectedFolder))
    .join('');
  const content = folders || '<span class="script-tree-empty">No folders found</span>';
  const selected = selectedFolder.length === 0;
  return `<div class="script-tree" data-script-tree aria-label="Script folder browser"><button class="script-tree-root${selected ? ' selected' : ''}" type="button" data-select-script-folder=""><span class="script-tree-folder-icon" aria-hidden="true"></span><strong>Script library</strong><small>${scriptTreeFileCount(root)} ${scriptTreeFileCount(root) === 1 ? 'file' : 'files'}</small></button><div class="script-tree-root-children">${content}</div></div>`;
}

function renderScriptFolderPreview(root: ScriptTreeFolder): string {
  const selectedFolder = resolveSelectedScriptFolder(root);
  const folder = findScriptTreeFolder(root, selectedFolder) ?? root;
  const extensions = [...new Set(folder.scripts.map(scriptExtension).filter(Boolean))].sort();
  if (scriptPreviewExtension !== 'all' && !extensions.includes(scriptPreviewExtension)) {
    scriptPreviewExtension = 'all';
    scriptPreviewPage = 1;
  }
  const childFolders = [...folder.folders.values()].sort((left, right) => left.name.localeCompare(right.name));
  const scripts = [...folder.scripts]
    .filter((script) => scriptPreviewExtension === 'all' || scriptExtension(script) === scriptPreviewExtension)
    .sort((left, right) => left.name.localeCompare(right.name));
  const entries = [
    ...childFolders.map((child) => ({ kind: 'folder' as const, folder: child })),
    ...scripts.map((script) => ({ kind: 'script' as const, script })),
  ];
  const pageCount = Math.max(1, Math.ceil(entries.length / scriptPreviewPageSize));
  scriptPreviewPage = Math.min(Math.max(1, scriptPreviewPage), pageCount);
  const pageEntries = entries.slice((scriptPreviewPage - 1) * scriptPreviewPageSize, scriptPreviewPage * scriptPreviewPageSize);
  const rows = pageEntries.map((entry) => {
    if (entry.kind === 'folder') {
      const fileCount = scriptTreeFileCount(entry.folder);
      return `<button class="script-preview-row script-preview-folder" type="button" data-select-script-folder="${escapeHtml(entry.folder.path)}"><span class="script-tree-folder-icon" aria-hidden="true"></span><span class="script-preview-copy"><strong>${escapeHtml(entry.folder.name)}</strong><small>Folder · ${fileCount} ${fileCount === 1 ? 'file' : 'files'}</small></span><span class="script-preview-action">Open</span></button>`;
    }
    const script = entry.script;
    const extension = scriptExtension(script).replace('.', '').slice(0, 4).toUpperCase() || 'FILE';
    return `<div class="script-preview-row script-preview-file" title="${escapeHtml(script.id)}"><span class="script-preview-file-type" aria-hidden="true">${escapeHtml(extension)}</span><span class="script-preview-copy"><strong>${escapeHtml(script.name)}</strong><small>${escapeHtml(script.duration)} · ${escapeHtml(script.size)} · ${escapeHtml(script.updated)}</small></span></div>`;
  }).join('');
  const extensionOptions = ['<option value="all">All suffixes</option>', ...extensions.map((extension) => `<option value="${escapeHtml(extension)}"${scriptPreviewExtension === extension ? ' selected' : ''}>${escapeHtml(extension)}</option>`)].join('');
  const folderName = folder.path ? folder.name : 'Script library';
  const folderPath = folder.path ? `/${folder.path}` : '/';
  const itemLabel = `${entries.length} ${entries.length === 1 ? 'item' : 'items'}`;
  return `<div class="script-preview-heading"><div><p class="eyebrow">Folder preview</p><h2>${escapeHtml(folderName)}</h2><p class="script-preview-path" title="${escapeHtml(folderPath)}">${escapeHtml(folderPath)}</p></div><span class="result-count">${itemLabel}</span></div><div class="script-preview-toolbar"><label for="script-extension-filter">Suffix<select id="script-extension-filter">${extensionOptions}</select></label><span>${scripts.length} ${scripts.length === 1 ? 'file' : 'files'}</span></div><div class="script-preview-list" aria-label="Contents of ${escapeHtml(folderName)}">${rows || '<div class="script-preview-empty"><strong>This folder is empty</strong><span>No matching script files or subfolders.</span></div>'}</div><div class="script-preview-pagination"><span>Page ${scriptPreviewPage} of ${pageCount}</span><div><button class="icon-button" type="button" data-script-preview-page="${scriptPreviewPage - 1}" aria-label="Previous page" title="Previous page"${scriptPreviewPage <= 1 ? ' disabled' : ''}>&#8592;</button><button class="icon-button" type="button" data-script-preview-page="${scriptPreviewPage + 1}" aria-label="Next page" title="Next page"${scriptPreviewPage >= pageCount ? ' disabled' : ''}>&#8594;</button></div></div>`;
}

function updateScriptFolderBrowser(state: AppState): void {
  if (state.route !== 'scripts') return;
  const root = buildScriptTree(state.scripts, state.scriptFolders ?? []);
  const selectedFolder = resolveSelectedScriptFolder(root);
  const preview = appRoot.querySelector<HTMLElement>('[data-script-folder-preview]');
  if (preview) preview.innerHTML = renderScriptFolderPreview(root);
  appRoot.querySelector<HTMLElement>('.script-tree-root')?.classList.toggle('selected', selectedFolder.length === 0);
  appRoot.querySelectorAll<HTMLDetailsElement>('[data-script-tree-folder]').forEach((details) => {
    const path = details.dataset.scriptTreeFolder ?? '';
    details.querySelector<HTMLElement>(':scope > summary')?.classList.toggle('selected', path === selectedFolder);
    if (path === selectedFolder || selectedFolder.startsWith(`${path}/`)) details.open = true;
  });
}

function renderScripts(state: AppState): string {
  const fnosAccess = state.settings?.fnosScriptAccess;
  if (fnosAccess?.externalRootActive) clearPendingFnosScriptAuthorization();
  const pendingAuthorization = hasPendingFnosScriptAuthorization();
  const fnosStatus = !fnosAccess?.available ? '' : fnosAccess.restartRequired || pendingAuthorization
    ? 'Restart required'
    : fnosAccess.externalRootActive
      ? 'Active'
      : fnosAccess.externalRootConfigured
        ? 'Folder unavailable'
        : 'Not configured';
  const fnosStatusKind: Status | 'pass' | 'warn' = fnosAccess?.externalRootActive
    ? 'pass'
    : fnosAccess?.externalRootConfigured || pendingAuthorization
      ? 'warn'
      : 'offline';
  const fnosDetail = fnosAccess && fnosAccess.authorizedDirectoryCount > 1
    ? `${fnosAccess.authorizedDirectoryCount} folders authorized; the first folder is mounted.`
    : fnosAccess?.externalRootActive
      ? 'Mapped read-only; source files remain untouched.'
      : fnosAccess?.systemVersion
        ? `fnOS ${fnosAccess.systemVersion}`
        : 'fnOS application folder access';
  const fnosMode = fnosAccess?.available === true;
  const fnosFolderAccess = fnosMode ? `<div class="fnos-folder-access" data-fnos-script-access><div class="fnos-folder-status">${statusDot(fnosStatusKind)}<div><span>fnOS folder mapping</span><strong>${escapeHtml(fnosStatus)}</strong><small>${escapeHtml(fnosDetail)}</small></div></div><div class="fnos-folder-actions"><button class="button button-primary" type="button" data-action="authorize-fnos-scripts">Add mapping</button><button class="button button-secondary" type="button" data-action="remove-fnos-script-mapping"${fnosAccess.externalRootConfigured || pendingAuthorization ? '' : ' disabled'} title="Manage authorized folders in fnOS App Settings">Remove mapping</button></div></div>` : '';
  const root = buildScriptTree(state.scripts, state.scriptFolders ?? []);
  const selectedFolder = resolveSelectedScriptFolder(root);
  const folderSummary = `${scriptTreeFolderCount(root)} folders`;
  const scriptTree = renderScriptTree(root, selectedFolder);
  const pageActions = fnosMode ? '' : '<button class="button button-primary" data-action="import-script">Import script</button>';
  return `${pageHeading('Library', 'Scripts', 'Browse the script library used by routed source sessions.', pageActions)}
    <div class="script-layout"><section class="panel script-folders"><div class="panel-heading"><div><p class="eyebrow">Folders</p><h2>Script directories</h2></div><span class="result-count">${folderSummary}</span></div>${fnosFolderAccess}<form class="folder-add" data-folder-add><label class="sr-only" for="script-folder-name">New folder</label><input id="script-folder-name" type="text" placeholder="New folder, e.g. projects/2025" autocomplete="off" /><button class="button button-secondary" type="submit">Add folder</button></form>${scriptTree}</section><section class="panel script-folder-preview" data-script-folder-preview>${renderScriptFolderPreview(root)}</section></div>`;
}

function updateStatusBindings(state: AppState) {
  const main = appRoot.querySelector<HTMLElement>('.main-content');
  if (!main) return;
  const setText = (selector: string, value: string) => {
    const element = main.querySelector<HTMLElement>(selector);
    if (element && element.textContent !== value) element.textContent = value;
  };
  if (state.route === 'runtime') {
    setText('[data-bind="health-score"]', `${state.health.filter((check) => check.status === 'pass').length}/${state.health.length}`);
    setText('.metric-grid .metric-card:nth-child(4) strong', String(state.logs.length));
    main.querySelectorAll<HTMLElement>('[data-runtime-playback]').forEach((control) => {
      const source = state.media.find((item) => item.id === control.dataset.runtimePlayback);
      if (!source) return;
      const media = source.mediaState;
      const duration = Math.max(0, media?.durationSeconds ?? 0);
      const positionValue = Math.min(duration || Number.MAX_SAFE_INTEGER,
        Math.max(0, media?.positionSeconds ?? 0));
      const available = source.status === 'online' && media !== undefined;
      const dot = control.querySelector<HTMLElement>('[data-runtime-source-status]');
      if (dot) dot.className = `status-dot ${statusClass(source.status)}`;
      const path = control.querySelector<HTMLElement>('[data-runtime-media-path]');
      if (path) path.textContent = media?.path ?? source.endpoint;
      const position = control.querySelector<HTMLElement>('[data-runtime-source-position]');
      const durationLabel = control.querySelector<HTMLElement>('[data-runtime-source-duration]');
      if (position) position.textContent = formatTime(positionValue);
      if (durationLabel) durationLabel.textContent = duration > 0 ? formatTime(duration) : '--:--';
      const seek = control.querySelector<HTMLInputElement>('[data-runtime-source-seek]');
      if (seek) {
        seek.max = String(Math.max(0.1, duration));
        seek.disabled = !available || !source.controls.canSeek;
        if (document.activeElement !== seek) seek.value = String(positionValue);
      }
      const toggle = control.querySelector<HTMLButtonElement>('[data-runtime-source-toggle]');
      if (toggle) {
        toggle.textContent = media?.playing ? 'Pause' : 'Play';
        toggle.disabled = !available || !source.controls.canPlayPause;
      }
      const speed = control.querySelector<HTMLSelectElement>('[data-runtime-source-speed]');
      if (speed) {
        speed.disabled = !available || !source.controls.canSetPlaybackSpeed;
        if (document.activeElement !== speed) speed.value = String(media?.playbackSpeed ?? 1);
      }
      const scriptSelect = control.querySelector<HTMLSelectElement>(`[data-runtime-source-script="${source.id}"]`);
      if (scriptSelect && document.activeElement !== scriptSelect) {
        const route = state.runtimeRoutes.find((item) => item.sourceId === source.id);
        const scriptId = route?.scriptId ?? '';
        if (scriptSelect.value !== scriptId) scriptSelect.value = scriptId;
      }
    });
    const healthList = main.querySelector<HTMLElement>('.health-list');
    const healthRows = healthList?.querySelectorAll<HTMLElement>('[data-health-index]');
    if (healthList && healthRows?.length !== state.health.length) {
      healthList.innerHTML = renderHealthItems(state);
    } else {
      state.health.forEach((check, index) => {
        const row = healthRows?.item(index);
        const dot = row?.querySelector<HTMLElement>('.status-dot');
        const label = row?.querySelector<HTMLElement>('[data-health-label]');
        const detail = row?.querySelector<HTMLElement>('[data-health-detail]');
        if (dot) dot.className = `status-dot ${statusClass(check.status)}`;
        if (label && label.textContent !== check.label) label.textContent = check.label;
        if (detail && detail.textContent !== check.detail) detail.textContent = check.detail;
      });
    }
    const activity = main.querySelector<HTMLElement>('.activity-panel .log-list');
    const logs = state.logs.slice(0, 4);
    if (activity && activity.dataset.logSignature !== logSignature(logs)) {
      const template = document.createElement('template');
      template.innerHTML = renderLogTable(logs);
      const replacement = template.content.firstElementChild;
      if (replacement) activity.replaceWith(replacement);
    }
    return;
  }
  if (state.route === 'settings') {
    const targets = state.outputs;
    if (selectedAxisTargetId === null || !targets.some((t) => t.id === selectedAxisTargetId)) {
      selectedAxisTargetId = targets.find((t) => t.status === 'online')?.id ?? targets[0]?.id ?? null;
    }
    const activeTarget = targets.find((t) => t.id === selectedAxisTargetId);
    const isOnline = activeTarget?.status === 'online';

    const badge = main.querySelector<HTMLElement>('[data-axis-target-badge]');
    if (badge && activeTarget) {
      badge.className = `status-badge ${statusClass(activeTarget.status)}`;
      badge.innerHTML = `${statusDot(activeTarget.status)}<span data-axis-target-status-text>${statusLabel(activeTarget.status)}</span>`;
    }
    const statusLabelEl = main.querySelector<HTMLElement>('[data-axis-target-status-label]');
    if (statusLabelEl && activeTarget) {
      statusLabelEl.textContent = `${activeTarget.name} · ${activeTarget.endpoint}`;
    }
    const toggleButton = main.querySelector<HTMLButtonElement>('[data-axis-target-toggle]');
    if (toggleButton && activeTarget) {
      toggleButton.dataset.axisTargetToggle = activeTarget.id;
      toggleButton.className = `button ${isOnline ? 'button-danger' : 'button-primary'}`;
      toggleButton.innerHTML = `<span class="status-dot ${statusClass(activeTarget.status)}" aria-hidden="true"></span><span>${isOnline ? 'Disconnect' : activeTarget.status === 'busy' ? 'Connecting...' : 'Connect target'}</span>`;
      toggleButton.disabled = activeTarget.status === 'busy';
    }
    const liveHint = main.querySelector<HTMLElement>('[data-axis-target-live-hint]');
    if (liveHint) {
      liveHint.textContent = isOnline ? (followLiveTarget ? 'Live mirroring active' : 'Mirroring paused') : 'Device offline';
    }

    if (followLiveTarget) {
      const host = main.querySelector<HTMLElement>('[data-axis-model-host]');
      if (host && host.dataset.modelState === 'ready') {
        const live = state.axisValues;
        const liveValues: Partial<Record<ModeledAxisName, number>> = {};
        for (const name of modeledAxisNames) {
          if (live[name] !== undefined) liveValues[name] = live[name];
        }
        if (Object.keys(liveValues).length > 0) {
          updateOsrAxisPreview(liveValues);
          const currentAxisVal = liveValues[selectedAxisPreview];
          const modelOutput = main.querySelector<HTMLOutputElement>('[data-axis-preview-output]');
          if (modelOutput && currentAxisVal !== undefined) modelOutput.value = percent(currentAxisVal);
        }
      }
    }
    return;
  }
}

function renderOutputs(state: AppState): string {
  return `${pageHeading('Devices', 'Output targets', 'Keep protocol connections visible, bounded, and easy to recover.', '<button class="button button-primary" data-action="focus-output-form">Add target</button>')}
    <div class="notice notice-info"><strong>Bounded transports.</strong><span>TCP, UDP, WebSocket, file, serial, The Handy, and Buttplug targets use the server transport registry.</span></div>
    ${renderOutputForm()}
    <section class="panel table-panel" aria-labelledby="targets-title"><div class="panel-heading"><div><p class="eyebrow">Configured targets</p><h2 id="targets-title">All outputs</h2></div><span class="result-count">${state.outputs.length} targets</span></div><div class="responsive-table"><table><thead><tr><th scope="col">Target</th><th scope="col">Protocol</th><th scope="col">Status</th><th scope="col">Rate</th><th scope="col">Diagnostics</th><th scope="col">Last send</th><th scope="col"><span class="sr-only">Actions</span></th></tr></thead><tbody>${state.outputs.length === 0 ? '<tr><td colspan="7">No output targets configured.</td></tr>' : state.outputs.map((output) => `<tr><td><strong>${escapeHtml(output.name)}</strong><span class="table-subtext">${escapeHtml(output.endpoint)} · ${escapeHtml(displayAxis(output.sourceAxis))}</span>${output.error ? `<span class="table-subtext resource-error">${escapeHtml(output.error)}</span>` : ''}</td><td>${escapeHtml(output.protocol)}</td><td><span class="status-badge ${statusClass(output.status)}">${statusDot(output.status)}${statusLabel(output.status)}</span></td><td><span class="rate-value">${output.rate} Hz</span></td><td><span class="table-subtext">${output.sentFrames.toLocaleString()} sent / ${output.coalescedFrames.toLocaleString()} coalesced</span><span class="table-subtext">${output.failedFrames.toLocaleString()} failed / ${output.reconnects.toLocaleString()} reconnects</span></td><td>${escapeHtml(output.lastSentAt)}</td><td><div class="resource-actions"><button class="button button-secondary" data-output-retest="${escapeHtml(output.id)}">Test</button><button class="button button-secondary" data-output-toggle="${escapeHtml(output.id)}">${output.status === 'online' ? 'Disconnect' : 'Connect'}</button><button class="button button-secondary" data-output-edit="${escapeHtml(output.id)}">Edit</button><button class="button button-danger" data-output-delete="${escapeHtml(output.id)}">Delete</button></div></td></tr>`).join('')}</tbody></table></div></section>`;
}

function renderOutputForm(): string {
  return `<form class="resource-form panel" data-resource-form="output"><input type="hidden" name="id" /><div class="panel-heading"><div><p class="eyebrow">New device</p><h2>Register output target</h2></div><span class="result-count">Persisted on server</span></div><div class="resource-form-grid"><label for="output-name">Name <span class="optional-marker" aria-hidden="true">Optional</span><input id="output-name" name="name" placeholder="Generated from protocol" /></label><label for="output-protocol">Protocol<select id="output-protocol" name="protocol"><option value="tcp">TCP</option><option value="udp">UDP</option><option value="websocket">WebSocket</option><option value="file">File</option><option value="serial">Serial</option><option value="the-handy">The Handy</option><option value="buttplug">Buttplug.io</option></select></label><label for="output-endpoint">Endpoint<input id="output-endpoint" name="endpoint" required placeholder="Select a protocol to fill its default" /></label><label for="output-source-axis">Source axis <span class="optional-marker" aria-hidden="true">Optional</span><input id="output-source-axis" name="sourceAxis" pattern="[a-zA-Z0-9_-]*" placeholder="all (empty = all axes)" /></label><label for="output-rate">Update rate <span class="optional-marker" aria-hidden="true">Optional</span><input id="output-rate" name="rate" type="number" min="1" max="20" step="1" value="20" /></label><label for="output-device-name" data-output-field="buttplug">Buttplug device name <span class="optional-marker" aria-hidden="true">Optional</span><input id="output-device-name" name="deviceName" maxlength="256" placeholder="First discovered device" /></label><label for="output-device-index" data-output-field="buttplug">Buttplug device index <span class="optional-marker" aria-hidden="true">Optional</span><input id="output-device-index" name="deviceIndex" type="number" min="0" step="1" placeholder="Auto-select" /></label><label for="output-actuator-type" data-output-field="buttplug">Buttplug actuator <span class="optional-marker" aria-hidden="true">Optional</span><select id="output-actuator-type" name="actuatorType"><option value="Vibrate">Vibrate</option><option value="Oscillate">Oscillate</option><option value="Constrict">Constrict</option><option value="Inflate">Inflate</option><option value="Linear">Linear</option><option value="Rotate">Rotate</option></select></label><label for="output-actuator-index" data-output-field="buttplug">Actuator index <span class="optional-marker" aria-hidden="true">Optional</span><input id="output-actuator-index" name="actuatorIndex" type="number" min="0" step="1" value="0" /></label><label for="output-duration" data-output-field="buttplug">Linear duration <span class="optional-marker" aria-hidden="true">Optional</span><input id="output-duration" name="durationMilliseconds" type="number" min="1" max="60000" step="1" value="50" /></label></div><div class="form-assist"><button class="button button-secondary" type="button" data-action="discover-output">Scan devices</button><button class="button button-secondary" type="button" data-action="test-output-draft">Test connection</button><a class="form-help-link" data-handy-key-help href="https://www.handyfeeling.com/" target="_blank" rel="noopener noreferrer" hidden>Get connection key</a></div><div class="discovery-results" data-discovery-results="output" aria-live="polite" hidden></div><p class="form-hint">Serial scans stable Linux device paths. Buttplug scans through the selected WebSocket server. Other protocols use their common default endpoint and a direct connection test.</p><div class="resource-form-options"><label><input type="checkbox" name="enabled" checked /> Enabled</label><label><input type="checkbox" name="autoConnect" checked /> Connect on startup</label><button class="button button-primary" type="submit">Create target</button><button class="button button-secondary" type="button" data-resource-cancel="output">Cancel</button></div></form>`;
}

function renderDeviceSettings(state: AppState): string {
  return `${pageHeading('Devices', 'Axis settings', 'Tune the fixed TCode axis set and save it as one device configuration.', '<button class="button button-primary" data-action="focus-axis-form">Edit axis configuration</button>')}
    <div class="notice notice-info"><strong>One configuration, 11 fixed axes.</strong><span>All rows are validated together, persisted in one server file, and applied to routed output sessions as one snapshot.</span></div>
    <section class="panel axis-spatial-panel" aria-labelledby="axis-map-title"><div class="panel-heading"><div><p class="eyebrow">Mechanical preview</p><h2 id="axis-map-title">SR6 axis model</h2></div><span class="result-count">L0 / L1 / L2 / R0 / R1 / R2</span></div><div data-axis-visualization>${renderDeviceAxisVisualization(state.deviceAxes, state)}</div></section>
    ${renderDeviceAxisForm(state.deviceAxes)}
    ${renderServerSettings(state)}${renderCalibrationWizardModal()}`;
}

function renderDeviceAxisForm(axes: DeviceAxisSetting[]): string {
  const interpolationOptions = (selected: string) => ['Linear', 'Pchip', 'Makima', 'Step']
    .map((value) => `<option${value === selected ? ' selected' : ''}>${value}</option>`).join('');
  const rows = axes.map((axis) => `<tr data-axis-row="${escapeHtml(axis.axis)}"><td><strong>${escapeHtml(axis.axis)}</strong><span class="table-subtext">${escapeHtml(axisFriendlyNames[axis.axis] ?? 'Standard axis')}</span></td><td>${renderDualRange(axis, 'output', axis.minimum, axis.maximum)}</td><td><div class="axis-limit-control"><label class="axis-checkbox"><input data-axis-field="limitEnabled" aria-label="${escapeHtml(axis.axis)} limit enabled" type="checkbox"${axis.limitEnabled ? ' checked' : ''} /> Enable limit</label>${renderDualRange(axis, 'limit', axis.limitMinimum, axis.limitMaximum)}</div></td><td><div class="axis-behavior-control"><select data-axis-field="interpolation" aria-label="${escapeHtml(axis.axis)} interpolation">${interpolationOptions(axis.interpolation)}</select><label class="axis-checkbox"><input data-axis-field="inverted" aria-label="${escapeHtml(axis.axis)} inverted" type="checkbox"${axis.inverted ? ' checked' : ''} /> Reverse direction</label></div></td><td><label class="axis-travel-input"><span class="sr-only">${escapeHtml(axis.axis)} maximum travel</span><input data-axis-field="maximumTravel" aria-label="${escapeHtml(axis.axis)} maximum travel" type="number" min="0.1" max="1000000" step="0.1" value="${axis.maximumTravel ?? ''}" placeholder="Not set" /><span>${axisTravelUnit(axis.axis)}</span></label></td></tr>`).join('');
  return `<form class="resource-form panel axis-configuration-form" data-resource-form="axis"><div class="panel-heading"><div><p class="eyebrow">Device configuration</p><h2>Set device axis behavior</h2></div><span class="result-count">${axes.length} fixed axes</span></div><div class="responsive-table axis-config-table"><table><thead><tr><th>Axis</th><th>Output range</th><th>Safety limit</th><th>Curve & direction</th><th>Full travel</th></tr></thead><tbody>${rows}</tbody></table></div><p class="form-hint">Drag both handles to set a range. Full travel is optional device calibration: millimeters for L axes, degrees for R axes, and percent for V/A axes.</p><div class="resource-form-options"><button class="button button-secondary" type="button" data-axis-reset-all>Reset all defaults</button><button class="button button-primary" type="submit">Save axis configuration</button></div></form>`;
}

function renderServerSettings(state: AppState): string {
  if (!state.settings) return '';
  const settings = state.settings;
  const migration = state.migration;
  const migrationState = migration?.state ?? 'unknown';
  const migrationDetail = migration?.error ?? (migrationState === 'disabled' ? 'No legacy desktop import was requested.' : 'No migration warnings reported.');
  return `<section class="content-grid diagnostics-settings-grid">
    <section class="panel" aria-labelledby="repository-settings-title"><div class="panel-heading"><div><p class="eyebrow">Repository</p><h2 id="repository-settings-title">Server-managed storage</h2></div><span class="result-count">${state.scripts.length} scripts</span></div><dl class="settings-list"><div><dt>Scripts root</dt><dd title="${escapeHtml(settings.scriptsRoot)}">${escapeHtml(settings.scriptsRoot)}</dd></div><div><dt>Configuration root</dt><dd title="${escapeHtml(settings.configRoot)}">${escapeHtml(settings.configRoot)}</dd></div><div><dt>Logs root</dt><dd title="${escapeHtml(settings.logsRoot)}">${escapeHtml(settings.logsRoot)}</dd></div><div><dt>Keys root</dt><dd title="${escapeHtml(settings.keysRoot)}">${escapeHtml(settings.keysRoot)}</dd></div></dl><p class="settings-note">Paths are read-only here; uploads and log exports use the server boundary.</p></section>
    <section class="panel" aria-labelledby="general-settings-title"><div class="panel-heading"><div><p class="eyebrow">General</p><h2 id="general-settings-title">Effective runtime</h2></div><span class="status-badge status-online">Active</span></div><dl class="settings-list"><div><dt>Schema</dt><dd>v${settings.schemaVersion}</dd></div><div><dt>Runtime cadence</dt><dd>${settings.tickHz} Hz</dd></div><div><dt>Browser clients</dt><dd>${settings.maxWebSocketClients}</dd></div><div><dt>API surface</dt><dd>REST v1 + WebSocket</dd></div></dl><p class="settings-note">Cadence and client limits are host configuration values; changing them requires a controlled service restart.</p></section>
  </section>
  <section class="content-grid diagnostics-settings-grid">
    <section class="panel" aria-labelledby="security-settings-title"><div class="panel-heading"><div><p class="eyebrow">Security</p><h2 id="security-settings-title">Access policy</h2></div><span class="status-badge ${settings.authenticationRequired ? 'status-online' : 'status-warning'}">${settings.authenticationRequired ? 'Protected' : 'Review'}</span></div><dl class="settings-list"><div><dt>API authentication</dt><dd>${settings.authenticationRequired ? 'Required' : 'Disabled'}</dd></div><div><dt>Browser session</dt><dd>${settings.authenticationRequired ? 'Token-bound WebSocket' : 'No token configured'}</dd></div><div><dt>Token visibility</dt><dd>Never returned by API</dd></div><div><dt>Secret fields</dt><dd>Redacted from responses and logs</dd></div></dl><p class="settings-note">Secret values are intentionally absent from this view and from diagnostic exports.</p></section>
    <section class="panel" aria-labelledby="migration-settings-title"><div class="panel-heading"><div><p class="eyebrow">Migration</p><h2 id="migration-settings-title">Legacy configuration</h2></div><span class="status-badge ${migrationState === 'migrated' || migrationState === 'already-present' ? 'status-online' : migrationState === 'failed' ? 'status-warning' : 'status-offline'}">${escapeHtml(migrationState)}</span></div><dl class="settings-list"><div><dt>Source</dt><dd>${escapeHtml(migration?.sourceFile ?? 'Not configured')}</dd></div><div><dt>Target</dt><dd>${escapeHtml(migration?.targetFile ?? 'Unknown')}</dd></div><div><dt>Warnings</dt><dd>${migration?.warningCount ?? 0}</dd></div><div><dt>Details</dt><dd title="${escapeHtml(migrationDetail)}">${escapeHtml(migrationDetail)}</dd></div></dl></section>
  </section>`;
}

function renderLogs(state: AppState): string {
  return `${pageHeading('Diagnostics', 'Logs and health', 'Inspect the event stream and the checks that protect the output loop.', '<button class="button button-secondary" data-action="refresh-diagnostics">Refresh</button><button class="button button-secondary" data-action="download-logs">Export snapshot</button>')}
    <div class="health-summary"><div class="health-summary-copy"><p class="eyebrow">Overall status</p><h2><span class="health-orb" aria-hidden="true"></span>Operational with one warning</h2><p>Last complete check at ${escapeHtml(state.lastSync)}. The warning is isolated to the OFS relay negotiation.</p></div><div class="health-summary-grid">${state.health.map((check) => `<div><span>${statusDot(check.status)}${check.status === 'pass' ? 'Pass' : 'Review'}</span><strong>${check.label}</strong></div>`).join('')}</div></div>
    <section class="panel table-panel" aria-labelledby="logs-title"><div class="panel-heading"><div><p class="eyebrow">Event stream</p><h2 id="logs-title">Recent logs</h2></div><label class="compact-select">Level <select id="log-filter" aria-label="Filter log level"><option value="all">All events</option><option value="info">Info</option><option value="warn">Warnings</option><option value="error">Errors</option></select></label></div><div id="log-table">${renderLogTable(state.logs)}</div></section>
    ${renderServerDiagnostics(state)}`;
}

function renderServerDiagnostics(state: AppState): string {
  if (!state.settings) return '';
  const adapters = state.adapters;
  const readyAdapters = adapters?.adapters.filter((adapter) => adapter.implementationStatus === 'ready').length ?? 0;
  const plannedAdapters = adapters?.adapters.filter((adapter) => adapter.implementationStatus === 'planned').length ?? 0;
  const blockedAdapters = adapters?.adapters.filter((adapter) => adapter.implementationStatus === 'blocked' || adapter.implementationStatus === 'unsupported').length ?? 0;
  const adapterRows = adapters?.adapters.slice(0, 10).map((adapter) => {
    const status = adapter.implementationStatus === 'ready' ? 'pass' : 'warn';
    return `<tr><td><strong>${escapeHtml(adapter.id)}</strong><span class="table-subtext">${escapeHtml(adapter.kind)} · ${escapeHtml(adapter.integration)}</span></td><td><span class="status-badge ${statusClass(status)}">${statusDot(status)}${escapeHtml(adapter.implementationStatus)}</span></td><td>${escapeHtml(adapter.notes)}</td></tr>`;
  }).join('') ?? '<tr><td colspan="3">Adapter inventory unavailable</td></tr>';
  const serialRows = state.serialDevices.length === 0
    ? '<tr><td colspan="3">No stable serial devices discovered</td></tr>'
    : state.serialDevices.map((device) => `<tr><td><strong>${escapeHtml(device.stableId)}</strong></td><td>${escapeHtml(device.devicePath)}</td><td>${escapeHtml(device.linkTarget ?? 'n/a')}</td></tr>`).join('');
  return `${renderServerSettings(state)}
  <section class="content-grid two-thirds diagnostics-grid"><section class="panel table-panel" aria-labelledby="adapter-title"><div class="panel-heading"><div><p class="eyebrow">Capabilities</p><h2 id="adapter-title">Adapter inventory</h2></div><span class="result-count">${readyAdapters} ready · ${plannedAdapters} planned · ${blockedAdapters} blocked</span></div><div class="responsive-table"><table><thead><tr><th>Adapter</th><th>Status</th><th>Boundary</th></tr></thead><tbody>${adapterRows}</tbody></table></div></section><section class="panel table-panel" aria-labelledby="serial-title"><div class="panel-heading"><div><p class="eyebrow">Device discovery</p><h2 id="serial-title">Serial devices</h2></div><span class="result-count">${state.serialDevices.length} found</span></div><div class="responsive-table"><table><thead><tr><th>Stable ID</th><th>Path</th><th>Link target</th></tr></thead><tbody>${serialRows}</tbody></table></div></section></section>`;
}

function logSignature(logs: AppState['logs']): string {
  return logs.map((log) => log.id).join(':');
}

function renderLogTable(logs: AppState['logs']): string {
  return `<div class="log-list" data-log-signature="${logSignature(logs)}">${logs.map((log) => `<div class="log-row"><span class="log-time">${log.time}</span><span class="log-level level-${log.level}">${log.level}</span><span class="log-message">${escapeHtml(log.message)}</span><span class="log-source">${escapeHtml(log.source)}</span></div>`).join('')}</div>`;
}

function showToast(message: string, variant: 'success' | 'error' = 'success') {
  if (toastTimer !== undefined) window.clearTimeout(toastTimer);
  toastRegion.innerHTML = `<div class="toast toast-${variant}" role="status">${escapeHtml(message)}</div>`;
  toastTimer = window.setTimeout(() => {
    toastRegion.innerHTML = '';
    toastTimer = undefined;
  }, 6000);
}

function saveBlob(blob: Blob, fileName: string) {
  const link = document.createElement('a');
  link.href = URL.createObjectURL(blob);
  link.download = fileName;
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(link.href), 1000);
}

function scriptIdForFile(file: File): string {
  const originalName = file.name.split(/[\\/]/).pop() ?? 'uploaded-script.funscript';
  const safeName = originalName.replace(/[^a-zA-Z0-9._-]/g, '-');
  return /\.(funscript|json)$/i.test(safeName) ? safeName : `${safeName || 'uploaded-script'}.funscript`;
}

async function importSelectedFile() {
  const file = importInput.files?.[0];
  if (!file) return;
  const id = scriptIdForFile(file);
  try {
    const content = await file.text();
    const script = await store.uploadScript(id, content);
    showToast(`Imported ${script.name}`);
  } catch (error) {
    const message = error instanceof Error ? error.message : 'The script could not be uploaded.';
    showToast(`Import failed: ${message}`, 'error');
  } finally {
    importInput.value = '';
  }
}

function synchronizeEndpointDefault(form: HTMLFormElement, fieldName: string, nextDefault: string,
  applyDefault: boolean) {
  const endpoint = form.elements.namedItem(fieldName) as HTMLInputElement | null;
  if (!endpoint) return;
  const previousDefault = endpoint.dataset.defaultEndpoint ?? '';
  if (applyDefault && (!endpoint.value.trim() || endpoint.value === previousDefault))
    endpoint.value = nextDefault;
  endpoint.dataset.defaultEndpoint = nextDefault;
}

function setDiscoveryResults(form: HTMLFormElement, kind: 'media' | 'output', html: string) {
  const results = form.querySelector<HTMLElement>(`[data-discovery-results="${kind}"]`);
  if (!results) return;
  results.innerHTML = html;
  results.hidden = !html;
}

function mediaApiKeyUrl(kind: string, endpoint: string): string | null {
  const root = endpoint.trim().replace(/\/+$/, '');
  if (kind === 'emby' && root) return `${root}/web/index.html#!/apikeys.html`;
  if (kind === 'jellyfin' && root) return `${root}/web/index.html#!/apikeys.html`;
  if (kind === 'plex') return 'https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token/';
  return null;
}

function synchronizeMediaForm(form: HTMLFormElement, applyDefault: boolean) {
  const kind = (form.elements.namedItem('kind') as HTMLSelectElement | null)?.value ?? 'internal';
  synchronizeEndpointDefault(form, 'endpoint', mediaEndpointDefaults[kind] ?? '', applyDefault);
  const endpoint = form.elements.namedItem('endpoint') as HTMLInputElement | null;
  const endpointLabel = form.querySelector<HTMLElement>('[data-media-field="endpoint"]');
  if (endpoint) endpoint.required = kind !== 'internal';
  if (endpointLabel) endpointLabel.hidden = kind === 'internal';
  const deviceField = form.querySelector<HTMLElement>('[data-media-field="device"]');
  if (deviceField) deviceField.hidden = !['emby', 'jellyfin', 'plex'].includes(kind);
  const secretField = form.querySelector<HTMLElement>('[data-media-field="secret"]');
  if (secretField) secretField.hidden = !(kind in mediaSecretDefaults);
  const secret = form.elements.namedItem('secretValue') as HTMLInputElement | null;
  if (secret) secret.placeholder = 'Paste the API key or token';
  const discover = form.querySelector<HTMLButtonElement>('[data-action="discover-media"]');
  if (discover) discover.hidden = !['emby', 'jellyfin'].includes(kind);
  const keyHelp = form.querySelector<HTMLAnchorElement>('[data-media-key-help]');
  const keyUrl = mediaApiKeyUrl(kind, endpoint?.value ?? '');
  if (keyHelp) {
    keyHelp.hidden = keyUrl === null;
    if (keyUrl) keyHelp.href = keyUrl;
    else keyHelp.removeAttribute('href');
  }
}

function synchronizeOutputForm(form: HTMLFormElement, applyDefault: boolean) {
  const protocol = (form.elements.namedItem('protocol') as HTMLSelectElement | null)?.value ?? 'tcp';
  const previousProtocol = form.dataset.currentProtocol;
  const endpoint = form.elements.namedItem('endpoint') as HTMLInputElement | null;
  if (applyDefault && previousProtocol && previousProtocol !== protocol && endpoint)
    endpoint.value = outputEndpointDefaults[protocol] ?? '';
  synchronizeEndpointDefault(form, 'endpoint', outputEndpointDefaults[protocol] ?? '', applyDefault);
  form.dataset.currentProtocol = protocol;
  form.querySelectorAll<HTMLElement>('[data-output-field="buttplug"]')
    .forEach((field) => { field.hidden = protocol !== 'buttplug'; });
  const discover = form.querySelector<HTMLButtonElement>('[data-action="discover-output"]');
  if (discover) discover.hidden = !['serial', 'buttplug'].includes(protocol);
  const handyHelp = form.querySelector<HTMLAnchorElement>('[data-handy-key-help]');
  if (handyHelp) handyHelp.hidden = protocol !== 'the-handy';
}

let generatedResourceSequence = 0;

function ensureResourceIdentity(form: HTMLFormElement, selectorName: 'kind' | 'protocol', suffix: string) {
  const id = form.elements.namedItem('id') as HTMLInputElement;
  const name = form.elements.namedItem('name') as HTMLInputElement;
  const selector = form.elements.namedItem(selectorName) as HTMLSelectElement;
  const typeLabel = selector.selectedOptions[0]?.textContent?.trim() || selector.value;
  if (!name.value.trim()) name.value = `${typeLabel} ${suffix}`;
  if (!id.value.trim()) {
    generatedResourceSequence++;
    const slug = name.value.trim().toLowerCase().replace(/[^a-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '')
      || selector.value.replace(/[^a-z0-9_-]+/g, '-') || suffix;
    id.value = `${slug.slice(0, 44)}-${Date.now().toString(36)}-${generatedResourceSequence.toString(36)}`;
  }
}

function readMediaSourceForm(form: HTMLFormElement): MediaSourceInput {
  ensureResourceIdentity(form, 'kind', 'source');
  const values = new FormData(form);
  const value = (name: string): string => String(values.get(name) ?? '').trim();
  return {
    id: value('id'), name: value('name'), kind: value('kind'), endpoint: value('endpoint'),
    enabled: values.get('enabled') === 'on', autoConnect: values.get('autoConnect') === 'on',
    deviceId: value('deviceId') || null, secretValue: value('secretValue') || null,
  };
}

function readOutputTargetForm(form: HTMLFormElement): OutputTargetInput {
  ensureResourceIdentity(form, 'protocol', 'target');
  const values = new FormData(form);
  const value = (name: string): string => String(values.get(name) ?? '').trim();
  const rawDeviceIndex = value('deviceIndex');
  return {
    id: value('id'), name: value('name'), protocol: value('protocol'), endpoint: value('endpoint'),
    sourceAxis: value('sourceAxis') || (['tcp', 'udp', 'websocket', 'serial', 'file'].includes(value('protocol')) ? '' : 'L0'),
    rate: Number(value('rate')) || 20,
    enabled: values.get('enabled') === 'on', autoConnect: values.get('autoConnect') === 'on',
    deviceName: value('deviceName') || null, deviceIndex: rawDeviceIndex ? Number(rawDeviceIndex) : null,
    actuatorType: value('actuatorType') || 'Vibrate', actuatorIndex: Number(value('actuatorIndex')) || 0,
    durationMilliseconds: Number(value('durationMilliseconds')) || 50,
  };
}

function outputApiRequest(input: OutputTargetInput) {
  return {
    id: input.id, name: input.name, protocol: input.protocol, endpoint: input.endpoint,
    sourceAxis: input.sourceAxis, updateRateHz: input.rate, enabled: input.enabled,
    autoConnect: input.autoConnect, deviceName: input.deviceName, deviceIndex: input.deviceIndex,
    actuatorType: input.actuatorType, actuatorIndex: input.actuatorIndex,
    durationMilliseconds: input.durationMilliseconds,
  };
}

function serverActionAvailable(): boolean {
  if (store.getState().mode === 'server') return true;
  showToast('Scanning and connection tests require the Server API.', 'error');
  return false;
}

async function discoverMedia(form: HTMLFormElement, button: HTMLButtonElement) {
  if (!serverActionAvailable()) return;
  const kind = (form.elements.namedItem('kind') as HTMLSelectElement).value;
  button.disabled = true;
  button.textContent = 'Scanning...';
  setDiscoveryResults(form, 'media', '<p class="discovery-message">Listening for servers on the local network...</p>');
  try {
    const candidates = await apiClient.discoverMediaServers(kind);
    const html = candidates.length === 0
      ? '<p class="discovery-message">No server replied. Confirm both hosts are on the same subnet and UDP 7359 is available.</p>'
      : candidates.map((candidate) => renderMediaDiscoveryCandidate(candidate)).join('');
    setDiscoveryResults(form, 'media', html);
  } catch (error) {
    const message = error instanceof Error ? error.message : 'Media server scan failed.';
    setDiscoveryResults(form, 'media', `<p class="discovery-message resource-error">${escapeHtml(message)}</p>`);
  } finally {
    button.disabled = false;
    button.textContent = 'Scan local network';
  }
}

function renderMediaDiscoveryCandidate(candidate: ApiMediaServerDiscovery): string {
  return `<article class="discovery-candidate"><div><strong>${escapeHtml(candidate.name)}</strong><span>${escapeHtml(candidate.endpoint)} · ${escapeHtml(candidate.sourceAddress)}</span></div><div class="discovery-candidate-actions"><button class="button button-secondary" type="button" data-media-candidate data-candidate-endpoint="${escapeHtml(candidate.endpoint)}" data-candidate-name="${escapeHtml(candidate.name)}" data-candidate-key-url="${escapeHtml(candidate.apiKeyUrl)}">Use</button>${candidate.apiKeyRequired ? `<a class="form-help-link" href="${escapeHtml(candidate.apiKeyUrl)}" target="_blank" rel="noopener noreferrer">Get API key</a>` : ''}</div></article>`;
}

async function discoverOutput(form: HTMLFormElement, button: HTMLButtonElement) {
  if (!serverActionAvailable()) return;
  const protocol = (form.elements.namedItem('protocol') as HTMLSelectElement).value;
  const endpoint = (form.elements.namedItem('endpoint') as HTMLInputElement).value.trim();
  button.disabled = true;
  button.textContent = 'Scanning...';
  setDiscoveryResults(form, 'output', '<p class="discovery-message">Scanning available devices...</p>');
  try {
    if (protocol === 'serial') {
      const devices = await apiClient.listSerialDevices();
      const html = devices.length === 0 ? '<p class="discovery-message">No stable serial devices were found.</p>'
        : devices.map((device) => `<article class="discovery-candidate"><div><strong>${escapeHtml(device.stableId)}</strong><span>${escapeHtml(device.devicePath)}${device.linkTarget ? ` · ${escapeHtml(device.linkTarget)}` : ''}</span></div><button class="button button-secondary" type="button" data-output-candidate data-candidate-endpoint="${escapeHtml(device.devicePath)}" data-candidate-name="${escapeHtml(device.stableId)}">Use</button></article>`).join('');
      setDiscoveryResults(form, 'output', html);
    } else if (protocol === 'buttplug') {
      const devices = await apiClient.discoverButtplugDevices(endpoint);
      const html = devices.length === 0 ? '<p class="discovery-message">The Buttplug server responded, but no devices were found.</p>'
        : devices.map((device) => `<article class="discovery-candidate"><div><strong>${escapeHtml(device.deviceName)}</strong><span>Device ${device.deviceIndex} · ${escapeHtml(device.actuatorTypes.join(', ') || 'No actuator metadata')}</span></div><button class="button button-secondary" type="button" data-output-candidate data-candidate-name="${escapeHtml(device.deviceName)}" data-candidate-index="${device.deviceIndex}" data-candidate-actuators="${escapeHtml(device.actuatorTypes.join(','))}">Use</button></article>`).join('');
      setDiscoveryResults(form, 'output', html);
    }
  } catch (error) {
    const message = error instanceof Error ? error.message : 'Device scan failed.';
    setDiscoveryResults(form, 'output', `<p class="discovery-message resource-error">${escapeHtml(message)}</p>`);
  } finally {
    button.disabled = false;
    button.textContent = 'Scan devices';
  }
}

async function testMediaDraft(form: HTMLFormElement, button: HTMLButtonElement) {
  if (!serverActionAvailable() || !form.reportValidity()) return;
  button.disabled = true;
  button.textContent = 'Testing...';
  try {
    await apiClient.testMediaSource(readMediaSourceForm(form));
    showToast('Media source connection test passed');
  } catch (error) {
    showToast(error instanceof Error ? error.message : 'Media source connection test failed.', 'error');
  } finally {
    button.disabled = false;
    button.textContent = 'Test connection';
  }
}

async function testOutputDraft(form: HTMLFormElement, button: HTMLButtonElement) {
  if (!serverActionAvailable() || !form.reportValidity()) return;
  button.disabled = true;
  button.textContent = 'Testing...';
  try {
    const input = readOutputTargetForm(form);
    await apiClient.testOutputTarget(outputApiRequest(input));
    const message = input.protocol === 'udp'
      ? 'UDP socket opened; the protocol cannot confirm a remote receiver.'
      : 'Output target connection test passed';
    showToast(message);
  } catch (error) {
    showToast(error instanceof Error ? error.message : 'Output target connection test failed.', 'error');
  } finally {
    button.disabled = false;
    button.textContent = 'Test connection';
  }
}

function currentAxisForm(): HTMLFormElement | null {
  return document.querySelector<HTMLFormElement>('[data-resource-form="axis"]');
}

function selectedAxisSetting(form: HTMLFormElement): DeviceAxisSetting {
  return axisOrDefault(readDeviceAxisForm(form), selectedAxisPreview);
}

function updateAxisPreviewPose(form: HTMLFormElement): void {
  const axis = selectedAxisSetting(form);
  const inputValue = axisPreviewPositions[selectedAxisPreview];
  const outputValue = applyAxisPreview(axis, inputValue);
  const positionInput = document.querySelector<HTMLInputElement>('[data-axis-preview-position]');
  const inputOutput = document.querySelector<HTMLOutputElement>('[data-axis-preview-input-value]');
  const modelOutput = document.querySelector<HTMLOutputElement>('[data-axis-preview-output]');
  if (positionInput && document.activeElement !== positionInput) positionInput.value = String(inputValue);
  if (positionInput) positionInput.setAttribute('aria-label', `${axis.axis} preview input position`);
  if (inputOutput) inputOutput.value = percent(inputValue);
  if (modelOutput) modelOutput.value = percent(outputValue);
  const values = Object.fromEntries(modeledAxisNames.map((name) => [name, name === selectedAxisPreview ? outputValue : 0.5])) as Record<ModeledAxisName, number>;
  updateOsrAxisPreview(values);
}

function synchronizeAxisPreviewControls(form: HTMLFormElement): void {
  const axis = selectedAxisSetting(form);
  document.querySelectorAll<HTMLButtonElement>('[data-axis-preview-select]').forEach((button) => {
    const active = button.dataset.axisPreviewSelect === selectedAxisPreview;
    button.classList.toggle('is-active', active);
    button.setAttribute('aria-selected', String(active));
  });
  const setText = (selector: string, value: string) => {
    const element = document.querySelector<HTMLElement>(selector);
    if (element) element.textContent = value;
  };
  setText('[data-axis-preview-name]', axis.axis);
  setText('[data-axis-preview-friendly]', axisFriendlyNames[axis.axis]);
  setText('[data-axis-preview-output-summary]', `${percent(axis.minimum)} to ${percent(axis.maximum)}`);
  setText('[data-axis-preview-limit-summary]', axis.limitEnabled
    ? `${percent(axis.limitMinimum)} to ${percent(axis.limitMaximum)}` : 'Disabled');

  const values: Record<string, number | boolean> = {
    minimum: axis.minimum,
    maximum: axis.maximum,
    limitEnabled: axis.limitEnabled,
    limitMinimum: axis.limitMinimum,
    limitMaximum: axis.limitMaximum,
    inverted: axis.inverted,
  };
  document.querySelectorAll<HTMLInputElement>('[data-axis-preview-field]').forEach((input) => {
    const field = input.dataset.axisPreviewField ?? '';
    const value = values[field];
    if (typeof value === 'boolean') input.checked = value;
    else if (typeof value === 'number' && document.activeElement !== input) input.value = String(value);
    const label = field === 'minimum' ? 'output minimum'
      : field === 'maximum' ? 'output maximum'
        : field === 'limitMinimum' ? 'limit minimum'
          : field === 'limitMaximum' ? 'limit maximum'
            : field === 'limitEnabled' ? 'limit enabled' : 'inverted';
    input.setAttribute('aria-label', `Preview ${axis.axis} ${label}`);
  });
  (['output', 'limit'] as const).forEach((group) => {
    const minimum = group === 'output' ? axis.minimum : axis.limitMinimum;
    const maximum = group === 'output' ? axis.maximum : axis.limitMaximum;
    const range = document.querySelector<HTMLElement>(`[data-axis-preview-range="${group}"]`);
    if (!range) return;
    range.style.setProperty('--range-start', `${minimum * 100}%`);
    range.style.setProperty('--range-end', `${maximum * 100}%`);
    range.classList.toggle('is-disabled', group === 'limit' && !axis.limitEnabled);
    const outputs = range.querySelectorAll<HTMLOutputElement>('[data-axis-preview-range-value]');
    if (outputs[0]) outputs[0].value = percent(minimum);
    if (outputs[1]) outputs[1].value = percent(maximum);
  });
  const summary = document.querySelector<HTMLElement>('.axis-preview-travel');
  if (summary) {
    summary.dataset.axisTravelSummary = axis.axis;
    summary.textContent = axisTravelSummary(axis);
  }
  updateAxisPreviewPose(form);
}

function applyAxisPreviewField(input: HTMLInputElement): void {
  const field = input.dataset.axisPreviewField;
  const form = currentAxisForm();
  const row = form?.querySelector<HTMLElement>(`[data-axis-row="${selectedAxisPreview}"]`);
  const target = field ? row?.querySelector<HTMLInputElement | HTMLSelectElement>(`[data-axis-field="${field}"]`) : null;
  if (!field || !form || !row || !target) return;
  if (target instanceof HTMLInputElement && target.type === 'checkbox') target.checked = input.checked;
  else target.value = input.value;
  synchronizeAxisRow(row, field);
  synchronizeAxisPreviewControls(form);
}

function stopAxisPreviewSweep(): void {
  if (axisPreviewSweepTimer !== undefined) window.clearInterval(axisPreviewSweepTimer);
  axisPreviewSweepTimer = undefined;
  const button = document.querySelector<HTMLButtonElement>('[data-axis-preview-sweep]');
  if (button) button.textContent = 'Sweep axis';
}

function setAxisPreviewSimulationUi(running: boolean, status = '6 axes'): void {
  const button = document.querySelector<HTMLButtonElement>('[data-axis-preview-simulate]');
  const statusElement = document.querySelector<HTMLElement>('[data-axis-preview-simulation-status]');
  if (button) button.textContent = running ? 'Stop simulation' : 'Simulate all axes';
  if (statusElement) statusElement.textContent = status;
  document.querySelectorAll<HTMLInputElement | HTMLButtonElement>('[data-axis-preview-select], [data-axis-preview-preset], [data-axis-preview-position], [data-axis-preview-field], [data-axis-preview-sweep]')
    .forEach((control) => { control.disabled = running; });
}

function stopAxisPreviewSimulation(): void {
  if (axisPreviewSimulationTimer !== undefined) window.clearInterval(axisPreviewSimulationTimer);
  axisPreviewSimulationTimer = undefined;
  axisPreviewSimulationAxes = [];
  axisPreviewSimulationAxisIndex = 0;
  axisPreviewSimulationPhase = 0;
  const restore = axisPreviewSimulationRestore;
  axisPreviewSimulationRestore = null;
  if (restore) {
    selectedAxisPreview = restore.axis;
    Object.assign(axisPreviewPositions, restore.positions);
  }
  const form = currentAxisForm();
  if (restore && form) synchronizeAxisPreviewControls(form);
  setAxisPreviewSimulationUi(false);
}

function stopAxisPreviewAnimations(): void {
  stopAxisPreviewSweep();
  stopAxisPreviewSimulation();
}

function toggleAxisPreviewSweep(): void {
  if (axisPreviewSweepTimer !== undefined) {
    stopAxisPreviewSweep();
    return;
  }
  const form = currentAxisForm();
  if (!form) return;
  axisPreviewSweepStartedAt = performance.now();
  const button = document.querySelector<HTMLButtonElement>('[data-axis-preview-sweep]');
  if (button) button.textContent = 'Stop sweep';
  axisPreviewSweepTimer = window.setInterval(() => {
    const phase = ((performance.now() - axisPreviewSweepStartedAt) % 2400) / 2400;
    axisPreviewPositions[selectedAxisPreview] = phase <= 0.5 ? phase * 2 : (1 - phase) * 2;
    updateAxisPreviewPose(form);
  }, 50);
}

function updateAxisPreviewSimulation(form: HTMLFormElement): void {
  const now = performance.now();
  const elapsed = Math.min(100, Math.max(0, now - axisPreviewSimulationLastFrameAt));
  axisPreviewSimulationLastFrameAt = now;
  axisPreviewSimulationPhase += elapsed / axisPreviewSimulationAxisDuration;
  if (axisPreviewSimulationPhase >= 1) {
    axisPreviewSimulationAxisIndex += 1;
    if (axisPreviewSimulationAxisIndex >= axisPreviewSimulationAxes.length) {
      stopAxisPreviewSimulation();
      return;
    }
    axisPreviewSimulationPhase = 0;
  }
  const axis = axisPreviewSimulationAxes[axisPreviewSimulationAxisIndex];
  if (selectedAxisPreview !== axis) {
    selectedAxisPreview = axis;
    synchronizeAxisPreviewControls(form);
  }
  const phase = axisPreviewSimulationPhase;
  axisPreviewPositions[axis] = phase < 0.25 ? 0.5 - phase * 2
    : phase < 0.75 ? (phase - 0.25) * 2
      : 1 - (phase - 0.75) * 2;
  const statusElement = document.querySelector<HTMLElement>('[data-axis-preview-simulation-status]');
  if (statusElement) statusElement.textContent = `${axis} · ${axisPreviewSimulationAxisIndex + 1}/${axisPreviewSimulationAxes.length}`;
  updateAxisPreviewPose(form);
}

function toggleAxisPreviewSimulation(): void {
  if (axisPreviewSimulationTimer !== undefined) {
    stopAxisPreviewSimulation();
    return;
  }
  const form = currentAxisForm();
  if (!form) return;
  stopAxisPreviewSweep();
  const selectedIndex = modeledAxisNames.indexOf(selectedAxisPreview);
  axisPreviewSimulationAxes = [
    ...modeledAxisNames.slice(selectedIndex),
    ...modeledAxisNames.slice(0, selectedIndex),
  ];
  axisPreviewSimulationRestore = {
    axis: selectedAxisPreview,
    positions: { ...axisPreviewPositions },
  };
  axisPreviewSimulationAxisIndex = 0;
  axisPreviewSimulationPhase = 0;
  axisPreviewSimulationLastFrameAt = performance.now();
  setAxisPreviewSimulationUi(true);
  updateAxisPreviewSimulation(form);
  axisPreviewSimulationTimer = window.setInterval(() => updateAxisPreviewSimulation(form), 50);
}

function handleRoutingPointerDown(event: PointerEvent) {
  const port = (event.target as Element).closest<HTMLElement>('[data-route-source-port]');
  const sourceId = port?.dataset.routeSourcePort;
  if (!port || !sourceId || event.button !== 0) return;
  routingDrag = { sourceId, pointerId: event.pointerId };
  port.setPointerCapture(event.pointerId);
  document.body.classList.add('is-routing');
  drawRuntimeWires({ x: event.clientX, y: event.clientY });
  event.preventDefault();
}

function handleRoutingPointerMove(event: PointerEvent) {
  if (!routingDrag || routingDrag.pointerId !== event.pointerId) return;
  drawRuntimeWires({ x: event.clientX, y: event.clientY });
  const hovered = document.elementFromPoint(event.clientX, event.clientY)
    ?.closest<HTMLElement>('[data-route-output-port]');
  document.querySelectorAll<HTMLElement>('[data-route-output-port]')
    .forEach((port) => port.classList.toggle('is-drop-target', port === hovered));
}

function handleRoutingPointerUp(event: PointerEvent) {
  if (!routingDrag || routingDrag.pointerId !== event.pointerId) return;
  const sourceId = routingDrag.sourceId;
  const cancelled = event.type === 'pointercancel';
  routingDrag = null;
  document.body.classList.remove('is-routing');
  document.querySelectorAll<HTMLElement>('[data-route-output-port]')
    .forEach((port) => port.classList.remove('is-drop-target'));
  if (cancelled) {
    drawRuntimeWires();
    return;
  }
  const outputId = document.elementFromPoint(event.clientX, event.clientY)
    ?.closest<HTMLElement>('[data-route-output-port]')?.dataset.routeOutputPort;
  if (!outputId) {
    drawRuntimeWires();
    return;
  }
  const assigned = outputAssignment(store.getState(), outputId);
  if (assigned === sourceId) {
    drawRuntimeWires();
    return;
  }
  void setRuntimeOutputAssignment(outputId, sourceId)
    .then(() => showToast(assigned ? 'Runtime route replaced' : 'Runtime route saved'))
    .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Runtime route failed.', 'error'));
}

function handleClick(event: MouseEvent) {
  const target = event.target as HTMLElement;
  const route = target.closest<HTMLElement>('[data-route]')?.dataset.route as Route | undefined;
  if (route) {
    store.setRoute(route);
    window.scrollTo({ top: 0, left: 0, behavior: 'auto' });
    document.querySelector<HTMLElement>('.main-content')?.focus({ preventScroll: true });
    return;
  }
  if (target.closest('[data-axis-wizard-open]')) {
    event.preventDefault();
    stopAxisPreviewAnimations();
    wizardState.active = true;
    wizardState.step = 'select-axes';
    wizardState.selectedAxes = modeledAxisNames.slice(0, 6);
    wizardState.currentAxisIndex = 0;
    wizardState.results = {};
    wizardState.backupAxes = store.getState().deviceAxes.map((a) => ({ ...a }));
    render(store.getState(), 'full');
    return;
  }
  if (target.closest('[data-axis-wizard-cancel]')) {
    event.preventDefault();
    wizardState.active = false;
    syncWizardPose('L0', 0.5);
    render(store.getState(), 'full');
    return;
  }
  const wizardPreset = target.closest<HTMLElement>('[data-axis-wizard-preset]')?.dataset.axisWizardPreset;
  if (wizardPreset) {
    event.preventDefault();
    if (wizardPreset === '6axes') wizardState.selectedAxes = modeledAxisNames.slice(0, 6);
    else if (wizardPreset === 'all') wizardState.selectedAxes = [...modeledAxisNames];
    else if (wizardPreset === 'none') wizardState.selectedAxes = [];
    render(store.getState(), 'full');
    return;
  }
  if (target.closest('[data-axis-wizard-start-calibration]')) {
    event.preventDefault();
    if (wizardState.selectedAxes.length === 0) return;
    wizardState.step = 'calibrating-min';
    wizardState.currentAxisIndex = 0;
    const axis = wizardState.selectedAxes[0];
    const existing = store.getState().deviceAxes.find((a) => a.axis === axis);
    wizardState.tempMin = existing ? existing.minimum : 0.2;
    syncWizardPose(axis, wizardState.tempMin);
    render(store.getState(), 'full');
    return;
  }
  if (target.closest('[data-axis-wizard-next="min"]')) {
    event.preventDefault();
    wizardState.step = 'calibrating-max';
    const axis = wizardState.selectedAxes[wizardState.currentAxisIndex];
    const existing = store.getState().deviceAxes.find((a) => a.axis === axis);
    wizardState.tempMax = Math.max(Number((wizardState.tempMin + 0.05).toFixed(2)), existing ? existing.maximum : 0.8);
    syncWizardPose(axis, wizardState.tempMax);
    render(store.getState(), 'full');
    return;
  }
  if (target.closest('[data-axis-wizard-prev="max"]')) {
    event.preventDefault();
    wizardState.step = 'calibrating-min';
    const axis = wizardState.selectedAxes[wizardState.currentAxisIndex];
    syncWizardPose(axis, wizardState.tempMin);
    render(store.getState(), 'full');
    return;
  }
  if (target.closest('[data-axis-wizard-next="max"]')) {
    event.preventDefault();
    const axis = wizardState.selectedAxes[wizardState.currentAxisIndex];
    wizardState.results[axis] = {
      minimum: wizardState.tempMin,
      maximum: wizardState.tempMax,
      limitMinimum: Math.max(0, Number((wizardState.tempMin - 0.01).toFixed(2))),
      limitMaximum: Math.min(1, Number((wizardState.tempMax + 0.01).toFixed(2))),
      limitEnabled: true,
    };
    wizardState.currentAxisIndex += 1;
    if (wizardState.currentAxisIndex < wizardState.selectedAxes.length) {
      wizardState.step = 'calibrating-min';
      const nextAxis = wizardState.selectedAxes[wizardState.currentAxisIndex];
      const nextExisting = store.getState().deviceAxes.find((a) => a.axis === nextAxis);
      wizardState.tempMin = nextExisting ? nextExisting.minimum : 0.2;
      syncWizardPose(nextAxis, wizardState.tempMin);
    } else {
      wizardState.step = 'summary';
      syncWizardPose(axis, 0.5);
    }
    render(store.getState(), 'full');
    return;
  }
  const wizardAdjust = target.closest<HTMLElement>('[data-axis-wizard-adjust]')?.dataset.axisWizardAdjust;
  if (wizardAdjust) {
    event.preventDefault();
    const [targetType, deltaStr] = wizardAdjust.split(',');
    const delta = Number(deltaStr);
    const axis = wizardState.selectedAxes[wizardState.currentAxisIndex];
    if (targetType === 'min') {
      wizardState.tempMin = Math.max(0, Math.min(0.95, Number((wizardState.tempMin + delta).toFixed(2))));
      syncWizardPose(axis, wizardState.tempMin);
    } else {
      wizardState.tempMax = Math.max(Number((wizardState.tempMin + 0.01).toFixed(2)), Math.min(1, Number((wizardState.tempMax + delta).toFixed(2))));
      syncWizardPose(axis, wizardState.tempMax);
    }
    render(store.getState(), 'full');
    return;
  }
  const wizardSet = target.closest<HTMLElement>('[data-axis-wizard-set]')?.dataset.axisWizardSet;
  if (wizardSet) {
    event.preventDefault();
    const [targetType, valStr] = wizardSet.split(',');
    const val = Number(valStr);
    const axis = wizardState.selectedAxes[wizardState.currentAxisIndex];
    if (targetType === 'min') {
      wizardState.tempMin = Math.max(0, Math.min(0.95, val));
      syncWizardPose(axis, wizardState.tempMin);
    } else {
      wizardState.tempMax = Math.max(Number((wizardState.tempMin + 0.01).toFixed(2)), Math.min(1, val));
      syncWizardPose(axis, wizardState.tempMax);
    }
    render(store.getState(), 'full');
    return;
  }
  const wizardSave = target.closest<HTMLElement>('[data-axis-wizard-save]')?.dataset.axisWizardSave;
  if (wizardSave) {
    event.preventDefault();
    applyWizardResultsToForm(wizardState.results);
    wizardState.active = false;
    syncWizardPose('L0', 0.5);
    if (wizardSave === 'apply') {
      const form = currentAxisForm();
      if (form) {
        const inputs = readDeviceAxisForm(form);
        void store.setDeviceAxes(inputs)
          .then(() => showToast('Calibrated axis configuration applied & saved!'))
          .catch((err: unknown) => showToast(err instanceof Error ? err.message : 'Calibration save failed', 'error'));
      }
    } else {
      showToast('Calibrated parameters saved to form draft.');
    }
    render(store.getState(), 'full');
    return;
  }
  if (target.closest('[data-axis-model-enable]')) {
    const host = document.querySelector<HTMLElement>('[data-axis-model-host]');
    const form = currentAxisForm();
    if (host && form) void mountOsrAxisPreview(host).then(() => {
      if (form.isConnected) updateAxisVisualization(form);
    });
    return;
  }
  const deleteFolderPath = target.closest<HTMLElement>('[data-delete-script-folder]')?.dataset.deleteScriptFolder;
  if (deleteFolderPath) {
    event.preventDefault();
    event.stopPropagation();
    void store.deleteFolder(deleteFolderPath)
      .then(() => showToast(`Removed folder ${deleteFolderPath}`))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Folder delete failed.', 'error'));
    return;
  }
  const axisPreviewSelect = target.closest<HTMLButtonElement>('[data-axis-preview-select]')?.dataset.axisPreviewSelect;
  if (axisPreviewSelect && modeledAxisNames.includes(axisPreviewSelect as ModeledAxisName)) {
    stopAxisPreviewAnimations();
    selectedAxisPreview = axisPreviewSelect as ModeledAxisName;
    const form = currentAxisForm();
    if (form) synchronizeAxisPreviewControls(form);
    return;
  }
  const axisPreviewPreset = target.closest<HTMLButtonElement>('[data-axis-preview-preset]');
  if (axisPreviewPreset) {
    stopAxisPreviewAnimations();
    axisPreviewPositions[selectedAxisPreview] = Number(axisPreviewPreset.dataset.axisPreviewPreset ?? 0.5);
    const form = currentAxisForm();
    if (form) updateAxisPreviewPose(form);
    return;
  }
  if (target.closest('[data-axis-preview-sweep]')) {
    toggleAxisPreviewSweep();
    return;
  }
  if (target.closest('[data-axis-preview-simulate]')) {
    toggleAxisPreviewSimulation();
    return;
  }
  const folderTarget = target.closest<HTMLElement>('[data-select-script-folder]');
  if (folderTarget) {
    const details = folderTarget.tagName === 'SUMMARY' ? folderTarget.parentElement as HTMLDetailsElement : null;
    const nextOpen = details ? !details.open : false;
    if (details) event.preventDefault();
    selectedScriptFolderPath = normalizeScriptTreePath(folderTarget.getAttribute('data-select-script-folder') ?? '');
    scriptPreviewExtension = 'all';
    scriptPreviewPage = 1;
    updateScriptFolderBrowser(store.getState());
    if (details) details.open = nextOpen;
    return;
  }
  const previewPage = target.closest<HTMLButtonElement>('[data-script-preview-page]')?.dataset.scriptPreviewPage;
  if (previewPage) {
    scriptPreviewPage = Number(previewPage);
    updateScriptFolderBrowser(store.getState());
    return;
  }
  const scriptMatch = target.closest<HTMLElement>('[data-runtime-script-match]');
  const scriptMatchSourceId = scriptMatch?.dataset.runtimeScriptMatch;
  const scriptMatchMode = scriptMatch?.dataset.scriptMatchMode as ScriptMatchMode | undefined;
  if (scriptMatchSourceId && scriptMatchMode) {
    void store.setRuntimeScriptMatchMode(scriptMatchSourceId, scriptMatchMode)
      .then((routes) => {
        const matched = routes.find((item) => item.sourceId === scriptMatchSourceId)?.scriptId;
        showToast(matched ? `${scriptMatchMode === 'fuzzy' ? 'Fuzzy' : scriptMatchMode === 'exact' ? 'Exact' : 'Manual'} script matched`
          : scriptMatchMode === 'manual' ? 'Manual script search ready' : `No ${scriptMatchMode} script match`);
        if (scriptMatchMode === 'manual')
          document.querySelector<HTMLInputElement>(`[data-runtime-script-search="${scriptMatchSourceId}"]`)?.focus();
      })
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Script matching failed.', 'error'));
    return;
  }
  const unrouteOutputId = target.closest<HTMLElement>('[data-runtime-unroute]')?.dataset.runtimeUnroute;
  if (unrouteOutputId) {
    void setRuntimeOutputAssignment(unrouteOutputId, null)
      .then(() => showToast('Runtime route removed'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Runtime route removal failed.', 'error'));
    return;
  }
  const runtimeSourceId = target.closest<HTMLElement>('[data-runtime-source-toggle]')?.dataset.runtimeSourceToggle;
  if (runtimeSourceId) {
    const source = store.getState().media.find((item) => item.id === runtimeSourceId);
    if (source?.mediaState) {
      const request = source.mediaState.playing
        ? store.pauseMediaSource(runtimeSourceId) : store.playMediaSource(runtimeSourceId);
      void request.catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Source playback command failed.', 'error'));
    }
    return;
  }
  const mediaCandidate = target.closest<HTMLButtonElement>('[data-media-candidate]');
  if (mediaCandidate) {
    const form = mediaCandidate.closest<HTMLFormElement>('[data-resource-form="media"]');
    if (form) {
      (form.elements.namedItem('endpoint') as HTMLInputElement).value = mediaCandidate.dataset.candidateEndpoint ?? '';
      const name = form.elements.namedItem('name') as HTMLInputElement;
      if (!name.value.trim()) name.value = mediaCandidate.dataset.candidateName ?? '';
      const keyHelp = form.querySelector<HTMLAnchorElement>('[data-media-key-help]');
      if (keyHelp && mediaCandidate.dataset.candidateKeyUrl) {
        keyHelp.href = mediaCandidate.dataset.candidateKeyUrl;
        keyHelp.hidden = false;
      }
      showToast('Discovered media server selected');
    }
    return;
  }
  const outputCandidate = target.closest<HTMLButtonElement>('[data-output-candidate]');
  if (outputCandidate) {
    const form = outputCandidate.closest<HTMLFormElement>('[data-resource-form="output"]');
    if (form) {
      const endpoint = outputCandidate.dataset.candidateEndpoint;
      if (endpoint) (form.elements.namedItem('endpoint') as HTMLInputElement).value = endpoint;
      const candidateName = outputCandidate.dataset.candidateName ?? '';
      const name = form.elements.namedItem('name') as HTMLInputElement;
      if (!name.value.trim()) name.value = candidateName;
      if (outputCandidate.dataset.candidateIndex) {
        (form.elements.namedItem('deviceName') as HTMLInputElement).value = candidateName;
        (form.elements.namedItem('deviceIndex') as HTMLInputElement).value = outputCandidate.dataset.candidateIndex;
        const actuators = outputCandidate.dataset.candidateActuators?.split(',') ?? [];
        const actuator = form.elements.namedItem('actuatorType') as HTMLSelectElement;
        if (actuators.includes('LinearCmd')) actuator.value = 'Linear';
        else if (actuators.includes('RotateCmd')) actuator.value = 'Rotate';
      }
      showToast('Discovered output device selected');
    }
    return;
  }
  const action = target.closest<HTMLElement>('[data-action]')?.dataset.action;
  if (action === 'discover-media') {
    const form = target.closest<HTMLFormElement>('[data-resource-form="media"]');
    const button = target.closest<HTMLButtonElement>('button');
    if (form && button) void discoverMedia(form, button);
    return;
  }
  if (action === 'discover-output') {
    const form = target.closest<HTMLFormElement>('[data-resource-form="output"]');
    const button = target.closest<HTMLButtonElement>('button');
    if (form && button) void discoverOutput(form, button);
    return;
  }
  if (action === 'test-media-draft') {
    const form = target.closest<HTMLFormElement>('[data-resource-form="media"]');
    const button = target.closest<HTMLButtonElement>('button');
    if (form && button) void testMediaDraft(form, button);
    return;
  }
  if (action === 'test-output-draft') {
    const form = target.closest<HTMLFormElement>('[data-resource-form="output"]');
    const button = target.closest<HTMLButtonElement>('button');
    if (form && button) void testOutputDraft(form, button);
    return;
  }
  if (action === 'refresh') showToast('Demo snapshot refreshed');
  if (action === 'focus-media-form') {
    document.querySelector<HTMLInputElement>('[data-resource-form="media"] input[name="name"]')?.focus();
  }
  if (action === 'focus-output-form') {
    document.querySelector<HTMLInputElement>('[data-resource-form="output"] input[name="name"]')?.focus();
  }
  if (action === 'focus-axis-form') {
    document.querySelector<HTMLInputElement>('[data-resource-form="axis"] [data-axis-field="minimum"]')?.focus();
  }
  const mediaEditId = target.closest<HTMLElement>('[data-media-edit]')?.dataset.mediaEdit;
  if (mediaEditId) {
    const source = store.getState().media.find((item) => item.id === mediaEditId);
    const form = document.querySelector<HTMLFormElement>('[data-resource-form="media"]');
    if (source && form) {
      form.dataset.editingId = source.id;
      (form.elements.namedItem('id') as HTMLInputElement).value = source.id;
      (form.elements.namedItem('name') as HTMLInputElement).value = source.name;
      (form.elements.namedItem('kind') as HTMLSelectElement).value = source.kind.toLowerCase();
      (form.elements.namedItem('endpoint') as HTMLInputElement).value = source.endpoint === '(internal)' ? '' : source.endpoint;
      (form.elements.namedItem('deviceId') as HTMLInputElement).value = source.deviceId ?? '';
      const secretInput = form.elements.namedItem('secretValue') as HTMLInputElement | null;
      if (secretInput) {
        secretInput.value = '';
        secretInput.placeholder = source.hasSecretValue
          ? `Leave empty to keep ${source.secretValueHint ?? 'current key'}`
          : 'Paste the API key or token';
      }
      (form.elements.namedItem('enabled') as HTMLInputElement).checked = source.enabled ?? true;
      (form.elements.namedItem('autoConnect') as HTMLInputElement).checked = source.autoConnect ?? true;
      synchronizeMediaForm(form, false);
      const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]');
      if (submit) submit.textContent = 'Save source';
      form.scrollIntoView({ behavior: 'auto', block: 'center' });
      (form.elements.namedItem('name') as HTMLInputElement).focus();
    }
  }
  const outputEditId = target.closest<HTMLElement>('[data-output-edit]')?.dataset.outputEdit;
  if (outputEditId) {
    const output = store.getState().outputs.find((item) => item.id === outputEditId);
    const form = document.querySelector<HTMLFormElement>('[data-resource-form="output"]');
    if (output && form) {
      form.dataset.editingId = output.id;
      (form.elements.namedItem('id') as HTMLInputElement).value = output.id;
      (form.elements.namedItem('name') as HTMLInputElement).value = output.name;
      (form.elements.namedItem('protocol') as HTMLSelectElement).value = output.protocol.toLowerCase();
      (form.elements.namedItem('endpoint') as HTMLInputElement).value = output.endpoint;
      (form.elements.namedItem('sourceAxis') as HTMLInputElement).value = output.sourceAxis;
      (form.elements.namedItem('rate') as HTMLInputElement).value = String(output.rate);
      (form.elements.namedItem('enabled') as HTMLInputElement).checked = output.enabled ?? true;
      (form.elements.namedItem('autoConnect') as HTMLInputElement).checked = output.autoConnect ?? true;
      (form.elements.namedItem('deviceName') as HTMLInputElement).value = output.deviceName ?? '';
      (form.elements.namedItem('deviceIndex') as HTMLInputElement).value = output.deviceIndex == null ? '' : String(output.deviceIndex);
      (form.elements.namedItem('actuatorType') as HTMLSelectElement).value = output.actuatorType ?? 'Vibrate';
      (form.elements.namedItem('actuatorIndex') as HTMLInputElement).value = String(output.actuatorIndex ?? 0);
      (form.elements.namedItem('durationMilliseconds') as HTMLInputElement).value = String(output.durationMilliseconds ?? 50);
      synchronizeOutputForm(form, false);
      const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]');
      if (submit) submit.textContent = 'Save target';
      form.scrollIntoView({ behavior: 'auto', block: 'center' });
      (form.elements.namedItem('name') as HTMLInputElement).focus();
    }
  }
  const resetAxes = target.closest<HTMLElement>('[data-axis-reset-all]');
  if (resetAxes) {
    document.querySelectorAll<HTMLElement>('[data-axis-row]').forEach((row) => {
      const setValue = (field: string, value: string) => {
        const input = row.querySelector<HTMLInputElement | HTMLSelectElement>(`[data-axis-field="${field}"]`);
        if (input) input.value = value;
      };
      setValue('minimum', '0');
      setValue('maximum', '1');
      setValue('interpolation', 'Linear');
      setValue('limitMinimum', '0');
      setValue('limitMaximum', '1');
      setValue('maximumTravel', '');
      const inverted = row.querySelector<HTMLInputElement>('[data-axis-field="inverted"]');
      const limitEnabled = row.querySelector<HTMLInputElement>('[data-axis-field="limitEnabled"]');
      if (inverted) inverted.checked = false;
      if (limitEnabled) limitEnabled.checked = false;
      synchronizeAxisRow(row);
    });
    const form = resetAxes.closest<HTMLFormElement>('[data-resource-form="axis"]');
    if (form) updateAxisVisualization(form);
  }
  const cancelKind = target.closest<HTMLElement>('[data-resource-cancel]')?.dataset.resourceCancel;
  if (cancelKind) {
    const form = document.querySelector<HTMLFormElement>(`[data-resource-form="${cancelKind}"]`);
    if (form) {
      delete form.dataset.editingId;
      form.reset();
      const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]');
      if (submit) submit.textContent = cancelKind === 'media' ? 'Create source' : 'Create target';
      setDiscoveryResults(form, cancelKind === 'media' ? 'media' : 'output', '');
      if (cancelKind === 'media') synchronizeMediaForm(form, true);
      if (cancelKind === 'output') synchronizeOutputForm(form, true);
    }
  }
  if (action === 'import-script') importInput.click();
  if (action === 'authorize-fnos-scripts') {
    void authorizeFnosScriptFolder()
      .then(async (outcome) => {
        if (outcome.status === 'authorized') {
          await store.refreshDiagnostics();
          showToast('Folder authorized. Restart MultiFunPlayer to mount it.');
        } else if (outcome.status === 'redirected') {
          showToast('fnOS authorization opened in a new tab.');
        } else if (outcome.status === 'error') {
          showToast(outcome.message, 'error');
        }
      })
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'fnOS folder authorization failed.', 'error'));
  }
  if (action === 'remove-fnos-script-mapping') {
    void openFnosAppSettings()
      .then(() => showToast('Remove folder access in fnOS App Settings. Source files are not deleted.'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Unable to open fnOS application settings.', 'error'));
  }
  if (action === 'download-logs') {
    void store.downloadLogs()
      .then((blob) => {
        if (blob) saveBlob(blob, 'runtime.log');
        showToast('Diagnostic snapshot downloaded');
      })
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Log download failed.', 'error'));
  }
  if (action === 'refresh-diagnostics') {
    void store.refreshDiagnostics()
      .then(() => showToast('Diagnostics refreshed'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Diagnostics refresh failed.', 'error'));
  }
  const mediaId = target.closest<HTMLElement>('[data-media-toggle]')?.dataset.mediaToggle;
  if (mediaId) {
    const source = store.getState().media.find((item) => item.id === mediaId);
    if (source) {
      const request = source.status === 'online' ? store.disconnectMediaSource(mediaId) : store.connectMediaSource(mediaId);
      void request.catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Media source action failed.', 'error'));
    }
  }
  const mediaRetestId = target.closest<HTMLElement>('[data-media-retest]')?.dataset.mediaRetest;
  if (mediaRetestId && serverActionAvailable()) {
    void apiClient.retestMediaSource(mediaRetestId)
      .then(() => showToast('Saved media source connection test passed'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Media source connection test failed.', 'error'));
  }
  const mediaPlayId = target.closest<HTMLElement>('[data-media-play]')?.dataset.mediaPlay;
  if (mediaPlayId) {
    void store.playMediaSource(mediaPlayId).catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Media playback command failed.', 'error'));
  }
  const mediaPauseId = target.closest<HTMLElement>('[data-media-pause]')?.dataset.mediaPause;
  if (mediaPauseId) {
    void store.pauseMediaSource(mediaPauseId).catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Media playback command failed.', 'error'));
  }
  const mediaSeekId = target.closest<HTMLElement>('[data-media-seek]')?.dataset.mediaSeek;
  if (mediaSeekId) {
    const input = Array.from(document.querySelectorAll<HTMLInputElement>('[data-media-seek-value]'))
      .find((candidate) => candidate.dataset.mediaSeekValue === mediaSeekId);
    const position = Number(input?.value);
    if (!Number.isFinite(position) || position < 0) {
      showToast('Seek position must be a non-negative number.', 'error');
    } else {
      void store.seekMediaSource(mediaSeekId, position).catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Media seek failed.', 'error'));
    }
  }
  const axisTargetToggleId = target.closest<HTMLElement>('[data-axis-target-toggle]')?.dataset.axisTargetToggle;
  if (axisTargetToggleId) {
    const output = store.getState().outputs.find((item) => item.id === axisTargetToggleId);
    if (output) {
      const isOnline = output.status === 'online';
      const request = isOnline ? store.disconnectOutputTarget(axisTargetToggleId) : store.connectOutputTarget(axisTargetToggleId);
      void request
        .then(() => showToast(isOnline ? `Disconnected from ${output.name}` : `Connected to ${output.name}`))
        .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Output target action failed.', 'error'));
    }
    return;
  }
  const outputId = target.closest<HTMLElement>('[data-output-toggle]')?.dataset.outputToggle;
  if (outputId) {
    const output = store.getState().outputs.find((item) => item.id === outputId);
    if (output) {
      const request = output.status === 'online' ? store.disconnectOutputTarget(outputId) : store.connectOutputTarget(outputId);
      void request.catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Output target action failed.', 'error'));
    }
  }
  const outputRetestId = target.closest<HTMLElement>('[data-output-retest]')?.dataset.outputRetest;
  if (outputRetestId && serverActionAvailable()) {
    const output = store.getState().outputs.find((item) => item.id === outputRetestId);
    void apiClient.retestOutputTarget(outputRetestId)
      .then(() => showToast(output?.protocol.toLowerCase() === 'udp'
        ? 'UDP socket opened; the protocol cannot confirm a remote receiver.'
        : 'Saved output target connection test passed'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Output target connection test failed.', 'error'));
  }
  const mediaDeleteId = target.closest<HTMLElement>('[data-media-delete]')?.dataset.mediaDelete;
  if (mediaDeleteId) {
    void store.deleteMediaSource(mediaDeleteId)
      .then(() => showToast('Media source deleted'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Media source deletion failed.', 'error'));
  }
  const outputDeleteId = target.closest<HTMLElement>('[data-output-delete]')?.dataset.outputDelete;
  if (outputDeleteId) {
    void store.deleteOutputTarget(outputDeleteId)
      .then(() => showToast('Output target deleted'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Output target deletion failed.', 'error'));
  }
}

async function handleSubmit(event: SubmitEvent) {
  const form = event.target as HTMLFormElement;
  if (form.dataset.authForm) {
    event.preventDefault();
    const values = new FormData(form);
    const value = (name: string): string => String(values.get(name) ?? '').trim();
    const mode = (event.submitter as HTMLButtonElement | null)?.dataset.authMode ?? 'connect';
    try {
      if (mode === 'pair') {
        const pairingToken = value('pairingToken');
        const authToken = value('newAuthToken');
        if (!pairingToken || authToken.length < 16)
          throw new Error('Pairing requires a token and a new access token of at least 16 characters.');
        await apiClient.pair(pairingToken, authToken);
      } else {
        const authToken = value('accessToken');
        if (!authToken) throw new Error('Enter an access token or choose pairing.');
        apiClient.setToken(authToken);
      }
      await connectRemoteStore(true);
    } catch (error) {
      renderAuthScreen(error instanceof Error ? error.message : 'Authentication failed.');
    }
    return;
  }
  if (form.hasAttribute('data-folder-add')) {
    event.preventDefault();
    const input = form.querySelector<HTMLInputElement>('#script-folder-name');
    const path = input?.value.trim() ?? '';
    if (!path) return;
    void store.createFolder(path)
      .then(() => { if (input) input.value = ''; })
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Folder create failed.', 'error'));
    return;
  }
  const kind = form.dataset.resourceForm;
  if (kind !== 'media' && kind !== 'output' && kind !== 'axis') return;
  event.preventDefault();
  try {
    if (kind === 'media') {
      const input = readMediaSourceForm(form);
      const editingId = form.dataset.editingId;
      if (editingId) {
        await store.updateMediaSource(editingId, input);
        showToast('Media source updated');
      } else {
        await store.createMediaSource(input);
        showToast('Media source created');
      }
    } else if (kind === 'output') {
      const input = readOutputTargetForm(form);
      const editingId = form.dataset.editingId;
      if (editingId) {
        await store.updateOutputTarget(editingId, input);
        showToast('Output target updated');
      } else {
        await store.createOutputTarget(input);
        showToast('Output target created');
      }
    } else {
      const inputs = readDeviceAxisForm(form);
      if (inputs.length === 0) throw new Error('The fixed axis configuration is unavailable.');
      for (const input of inputs) {
        if (!input.axis || !Number.isFinite(input.minimum) || !Number.isFinite(input.maximum)
          || input.minimum >= input.maximum || input.minimum < 0 || input.maximum > 1)
          throw new Error(`${input.axis || 'Axis'} range must satisfy 0 <= minimum < maximum <= 1.`);
        if (!Number.isFinite(input.limitMinimum) || !Number.isFinite(input.limitMaximum)
          || input.limitMinimum >= input.limitMaximum || input.limitMinimum < 0 || input.limitMaximum > 1)
          throw new Error(`${input.axis} limit must satisfy 0 <= limit minimum < limit maximum <= 1.`);
        if (input.maximumTravel !== null && (!Number.isFinite(input.maximumTravel)
          || input.maximumTravel <= 0 || input.maximumTravel > 1_000_000))
          throw new Error(`${input.axis} maximum travel must be greater than 0 and no more than 1000000.`);
      }
      await store.updateDeviceAxes(inputs);
      showToast('Axis configuration saved');
      return;
    }
    delete form.dataset.editingId;
    form.reset();
    const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]');
    if (submit) submit.textContent = kind === 'media' ? 'Create source' : 'Create target';
    setDiscoveryResults(form, kind, '');
    if (kind === 'media') synchronizeMediaForm(form, true);
    if (kind === 'output') synchronizeOutputForm(form, true);
  } catch (error) {
    showToast(error instanceof Error ? error.message : 'Resource creation failed.', 'error');
  }
}

function readDeviceAxisForm(form: HTMLFormElement): DeviceAxisSetting[] {
  return Array.from(form.querySelectorAll<HTMLElement>('[data-axis-row]')).map((row): DeviceAxisSetting => {
    const numberValue = (field: string) => Number(row.querySelector<HTMLInputElement>(`[data-axis-field="${field}"]`)?.value);
    const checkedValue = (field: string) => row.querySelector<HTMLInputElement>(`[data-axis-field="${field}"]`)?.checked ?? false;
    const travelValue = row.querySelector<HTMLInputElement>('[data-axis-field="maximumTravel"]')?.value.trim() ?? '';
    return {
      axis: row.dataset.axisRow ?? '',
      minimum: numberValue('minimum'),
      maximum: numberValue('maximum'),
      inverted: checkedValue('inverted'),
      interpolation: row.querySelector<HTMLSelectElement>('[data-axis-field="interpolation"]')?.value ?? '',
      limitEnabled: checkedValue('limitEnabled'),
      limitMinimum: numberValue('limitMinimum'),
      limitMaximum: numberValue('limitMaximum'),
      maximumTravel: travelValue === '' ? null : Number(travelValue),
    };
  });
}

function synchronizeAxisRow(row: HTMLElement, changedField = '') {
  const synchronizeRange = (group: 'output' | 'limit', minimumField: string, maximumField: string) => {
    const minimumInput = row.querySelector<HTMLInputElement>(`[data-axis-field="${minimumField}"]`);
    const maximumInput = row.querySelector<HTMLInputElement>(`[data-axis-field="${maximumField}"]`);
    const range = row.querySelector<HTMLElement>(`[data-axis-range="${group}"]`);
    if (!minimumInput || !maximumInput || !range) return;
    let minimum = Number(minimumInput.value);
    let maximum = Number(maximumInput.value);
    if (minimum >= maximum) {
      if (changedField === minimumField) minimum = Math.max(0, maximum - 0.01);
      else maximum = Math.min(1, minimum + 0.01);
    }
    minimumInput.value = String(Number(minimum.toFixed(2)));
    maximumInput.value = String(Number(maximum.toFixed(2)));
    range.style.setProperty('--range-start', `${minimum * 100}%`);
    range.style.setProperty('--range-end', `${maximum * 100}%`);
    const minimumOutput = range.querySelector<HTMLOutputElement>('[data-range-value="minimum"]');
    const maximumOutput = range.querySelector<HTMLOutputElement>('[data-range-value="maximum"]');
    if (minimumOutput) minimumOutput.value = percent(minimum);
    if (maximumOutput) maximumOutput.value = percent(maximum);
  };
  synchronizeRange('output', 'minimum', 'maximum');
  synchronizeRange('limit', 'limitMinimum', 'limitMaximum');
  const limitEnabled = row.querySelector<HTMLInputElement>('[data-axis-field="limitEnabled"]')?.checked ?? false;
  row.querySelector<HTMLElement>('[data-axis-range="limit"]')?.classList.toggle('is-disabled', !limitEnabled);
}

function updateAxisVisualization(form: HTMLFormElement) {
  synchronizeAxisPreviewControls(form);
}

function handleInput(event: Event) {
  const target = event.target as HTMLInputElement | HTMLSelectElement;
  const wizardSelectAxis = (target as HTMLInputElement).dataset?.axisWizardSelect;
  if (wizardSelectAxis) {
    const checked = (target as HTMLInputElement).checked;
    if (checked && !wizardState.selectedAxes.includes(wizardSelectAxis)) {
      wizardState.selectedAxes = [...wizardState.selectedAxes, wizardSelectAxis];
    } else if (!checked && wizardState.selectedAxes.includes(wizardSelectAxis)) {
      wizardState.selectedAxes = wizardState.selectedAxes.filter((a) => a !== wizardSelectAxis);
    }
    const startBtn = document.querySelector<HTMLButtonElement>('[data-axis-wizard-start-calibration]');
    if (startBtn) {
      startBtn.disabled = wizardState.selectedAxes.length === 0;
      startBtn.textContent = `Start calibration (${wizardState.selectedAxes.length} axes) →`;
    }
    return;
  }
  const wizardSliderType = (target as HTMLInputElement).dataset?.axisWizardSlider;
  if (wizardSliderType) {
    const axis = wizardState.selectedAxes[wizardState.currentAxisIndex];
    if (wizardSliderType === 'min') {
      wizardState.tempMin = Number(target.value);
      const out = document.querySelector<HTMLOutputElement>('[data-axis-wizard-min-output]');
      if (out) out.value = percent(wizardState.tempMin);
      syncWizardPose(axis, wizardState.tempMin);
    } else {
      let val = Number(target.value);
      if (val < wizardState.tempMin + 0.01) {
        val = Number((wizardState.tempMin + 0.01).toFixed(2));
        target.value = String(val);
      }
      wizardState.tempMax = val;
      const out = document.querySelector<HTMLOutputElement>('[data-axis-wizard-max-output]');
      if (out) out.value = percent(wizardState.tempMax);
      syncWizardPose(axis, wizardState.tempMax);
    }
    return;
  }
  if (target instanceof HTMLSelectElement && target.hasAttribute('data-axis-target-select')) {
    selectedAxisTargetId = target.value;
    const form = currentAxisForm();
    if (form) updateAxisVisualization(form);
    return;
  }
  if (target instanceof HTMLInputElement && target.hasAttribute('data-axis-target-sync')) {
    followLiveTarget = target.checked;
    if (followLiveTarget) {
      stopAxisPreviewAnimations();
      const live = store.getState().axisValues;
      const liveValues: Partial<Record<ModeledAxisName, number>> = {};
      for (const name of modeledAxisNames) {
        if (live[name] !== undefined) liveValues[name] = live[name];
      }
      if (Object.keys(liveValues).length > 0) updateOsrAxisPreview(liveValues);
    } else {
      const form = currentAxisForm();
      if (form) updateAxisPreviewPose(form);
    }
    const liveHint = document.querySelector<HTMLElement>('[data-axis-target-live-hint]');
    const activeTarget = store.getState().outputs.find((t) => t.id === selectedAxisTargetId);
    const isOnline = activeTarget?.status === 'online';
    if (liveHint) {
      liveHint.textContent = isOnline ? (followLiveTarget ? 'Live mirroring active' : 'Mirroring paused') : 'Device offline';
    }
    return;
  }
  if (target instanceof HTMLInputElement && target.hasAttribute('data-axis-preview-position')) {
    stopAxisPreviewAnimations();
    if (followLiveTarget) {
      followLiveTarget = false;
      const syncCheckbox = document.querySelector<HTMLInputElement>('[data-axis-target-sync]');
      if (syncCheckbox) syncCheckbox.checked = false;
      const liveHint = document.querySelector<HTMLElement>('[data-axis-target-live-hint]');
      if (liveHint) liveHint.textContent = 'Manual preview';
    }
    axisPreviewPositions[selectedAxisPreview] = Number(target.value);
    const form = currentAxisForm();
    if (form) updateAxisPreviewPose(form);
    return;
  }
  if (target instanceof HTMLInputElement && target.dataset.axisPreviewField) {
    stopAxisPreviewAnimations();
    applyAxisPreviewField(target);
    return;
  }
  const runtimeScriptSearchSourceId = target.dataset.runtimeScriptSearch;
  if (runtimeScriptSearchSourceId && event.type === 'input') {
    const query = target.value.trim().toLowerCase();
    const select = target.closest<HTMLElement>('[data-runtime-playback]')
      ?.querySelector<HTMLSelectElement>(`[data-runtime-source-script="${runtimeScriptSearchSourceId}"]`);
    select?.querySelectorAll<HTMLOptionElement>('option').forEach((option, index) => {
      option.hidden = index > 0 && query.length > 0 && !option.textContent?.toLowerCase().includes(query);
    });
    return;
  }
  const assignmentOutputId = target.dataset.runtimeOutputAssignment;
  if (assignmentOutputId && event.type === 'change') {
    void setRuntimeOutputAssignment(assignmentOutputId, target.value || null)
      .then(() => showToast('Runtime route saved'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Runtime route failed.', 'error'));
    return;
  }
  const runtimeSeekSourceId = target.dataset.runtimeSourceSeek;
  if (runtimeSeekSourceId) {
    const control = target.closest<HTMLElement>('[data-runtime-playback]');
    const value = Number(target.value);
    const label = control?.querySelector<HTMLElement>('[data-runtime-source-position]');
    if (label && Number.isFinite(value)) label.textContent = formatTime(value);
    if (event.type === 'change' && Number.isFinite(value))
      void store.seekMediaSource(runtimeSeekSourceId, value)
        .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Source seek failed.', 'error'));
    return;
  }
  const runtimeScriptSourceId = target.dataset.runtimeSourceScript;
  if (runtimeScriptSourceId && event.type === 'change') {
    void store.setRuntimeScript(runtimeScriptSourceId, target.value || null)
      .then(() => showToast('Runtime script saved'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Script assignment failed.', 'error'));
    return;
  }
  const runtimeSpeedSourceId = target.dataset.runtimeSourceSpeed;
  if (runtimeSpeedSourceId && event.type === 'change') {
    void store.setMediaSourcePlaybackSpeed(runtimeSpeedSourceId, Number(target.value))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Playback speed change failed.', 'error'));
    return;
  }
  const runtimeLoopSourceId = target.dataset.runtimeSourceLoop;
  if (runtimeLoopSourceId && event.type === 'change' && target instanceof HTMLInputElement) {
    void store.setRuntimeLoop(runtimeLoopSourceId, target.checked)
      .then(() => showToast(target.checked ? 'Loop enabled' : 'Loop disabled'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Loop setting failed.', 'error'));
    return;
  }
  const axisRow = target.closest<HTMLElement>('[data-axis-row]');
  if (axisRow && target.dataset.axisField) {
    synchronizeAxisRow(axisRow, target.dataset.axisField);
    const form = axisRow.closest<HTMLFormElement>('[data-resource-form="axis"]');
    if (form) updateAxisVisualization(form);
  }
  if (target.id === 'media-kind' && event.type === 'change') {
    const form = target.closest<HTMLFormElement>('[data-resource-form="media"]');
    if (form) {
      synchronizeMediaForm(form, true);
      setDiscoveryResults(form, 'media', '');
    }
  }
  if (target.id === 'media-endpoint') {
    const form = target.closest<HTMLFormElement>('[data-resource-form="media"]');
    if (form) synchronizeMediaForm(form, false);
  }
  if (target.id === 'output-protocol' && event.type === 'change') {
    const form = target.closest<HTMLFormElement>('[data-resource-form="output"]');
    if (form) {
      synchronizeOutputForm(form, true);
      setDiscoveryResults(form, 'output', '');
    }
  }
  if (target.id === 'script-extension-filter' && event.type === 'change') {
    scriptPreviewExtension = target.value;
    scriptPreviewPage = 1;
    updateScriptFolderBrowser(store.getState());
    return;
  }
  if (target.id === 'log-filter') {
    const level = target.value;
    const logs = store.getState().logs.filter((log) => level === 'all' || log.level === level);
    const table = document.querySelector<HTMLDivElement>('#log-table');
    if (table) table.innerHTML = renderLogTable(logs);
  }
}

appRoot.addEventListener('click', handleClick);
appRoot.addEventListener('pointerdown', handleRoutingPointerDown);
appRoot.addEventListener('pointermove', handleRoutingPointerMove);
appRoot.addEventListener('pointerup', handleRoutingPointerUp);
appRoot.addEventListener('pointercancel', handleRoutingPointerUp);
appRoot.addEventListener('submit', (event) => { void handleSubmit(event); });
appRoot.addEventListener('input', handleInput);
appRoot.addEventListener('change', handleInput);
importInput.addEventListener('change', () => { void importSelectedFile(); });
unsubscribeStore = store.subscribe(onStoreChanged);
document.addEventListener('visibilitychange', () => {
  if (document.hidden) {
    stopAxisPreviewAnimations();
    if (statusRenderTimer !== undefined) clearTimeout(statusRenderTimer);
    statusRenderTimer = undefined;
  }
  store.setVisible(!document.hidden);
  onStoreChanged(store.getState(), 'status');
});
window.addEventListener('pagehide', (event) => {
  if (event.persisted) store.setVisible(false); else store.close();
  stopAxisPreviewAnimations();
  destroyOsrAxisPreview();
  if (demoTimer !== undefined) clearInterval(demoTimer);
  if (runtimePollTimer !== undefined) clearTimeout(runtimePollTimer);
  if (statusRenderTimer !== undefined) clearTimeout(statusRenderTimer);
  demoTimer = runtimePollTimer = statusRenderTimer = undefined;
});
window.addEventListener('pageshow', (event) => {
  if (!event.persisted) return;
  store.setVisible(!document.hidden);
  onStoreChanged(store.getState(), 'full');
});
window.addEventListener('resize', () => {
  if (store.getState().route === 'runtime') drawRuntimeWires();
});
window.addEventListener('storage', (event) => {
  if (event.key !== fnosPendingAuthorizationKey || event.newValue !== '1') return;
  void store.refreshDiagnostics()
    .then(() => showToast('Folder authorized. Restart MultiFunPlayer to mount it.'))
    .catch(() => undefined);
});
const fnosAuthorizationCallback = consumeFnosAuthorizationCallback();
const initialRoute = window.location.hash.slice(1) as Route;

// A static preview remains useful without a backend. When served by ASP.NET
// Core, promote the same UI to the authenticated server store automatically.
void (async () => {
  if (new URLSearchParams(window.location.search).get('demo') === '1') return;
  await connectRemoteStore();
  if (fnosAuthorizationCallback?.status === 'authorized')
    showToast('Folder authorized. Restart MultiFunPlayer to mount it.');
  else if (fnosAuthorizationCallback?.status === 'error')
    showToast(fnosAuthorizationCallback.message, 'error');
})();

if (routes.some((route) => route.id === initialRoute)) store.setRoute(initialRoute);
if ('serviceWorker' in navigator) window.addEventListener('load', () => navigator.serviceWorker.register('./sw.js').catch(() => undefined));
