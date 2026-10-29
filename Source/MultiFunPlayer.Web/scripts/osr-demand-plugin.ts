import { createHash } from 'node:crypto';
import type { Plugin } from 'vite';

// osr-emu 0.7.0 exposes no way to stop its private, unconditional RAF loop.
// Adapt the pinned distribution at build time, in both dev and production.
// Fail closed on upgrades so a changed upstream loop cannot silently regress.
export function osrDemandPlugin(): Plugin {
  return {
    name: 'osr-demand-rendering',
    enforce: 'pre',
    transform(source, id) {
      if (!id.replaceAll('\\', '/').endsWith('/osr-emu/dist/osr-emu.dist.module.min.js')) return;
      const expected = 'ada4c9d457b5b5ec1c812310941908bbc079f5b3980af1fd5a253dbea8a79f87';
      if (createHash('sha256').update(source).digest('hex') !== expected)
        throw new Error('Review the osr-emu demand-rendering adapter before upgrading its distribution.');
      const replacements = [
        ['this.#S=(v?.model??"OSR2").toUpperCase(),this.#P()',
          'this.#S=(v?.model??"OSR2").toUpperCase(),this.renderOnDemand=v?.renderOnDemand??false,this.#P()'],
        ['#z(){this.#R=requestAnimationFrame(this.#z.bind(this)),this.controls.update(),this.#F(),this.#B()}',
          '#z(){this.renderOnDemand||(this.#R=requestAnimationFrame(this.#z.bind(this))),this.render()}render(){this.controls.update(),this.#F()}'],
      ];
      for (const [before, after] of replacements) {
        if (source.split(before).length !== 2) throw new Error('Unexpected osr-emu renderer structure.');
        source = source.replace(before, after);
      }
      return { code: source, map: null };
    },
  };
}
