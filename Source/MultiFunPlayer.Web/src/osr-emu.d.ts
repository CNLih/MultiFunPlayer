declare module 'osr-emu' {
  export type OSRModel = 'OSR2' | 'SR6' | 'SSR1';

  export interface OSREmulatorOptions {
    model?: OSRModel;
    scale?: Partial<Record<'L0' | 'L1' | 'L2' | 'R0' | 'R1' | 'R2', number>>;
    sceneHelpers?: boolean;
    objectHelpers?: boolean;
  }

  export class OSREmulator {
    constructor(element: HTMLElement | string, options?: OSREmulatorOptions);
    readonly axes: Record<string, number>;
    readonly renderer: { domElement: HTMLCanvasElement };
    write(input: string): void;
    destroy(): void;
  }

  export default OSREmulator;
}
