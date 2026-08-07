import {imageSize} from '../vendor/image-size/dist/index.mjs';

const ascii = (value) => Uint8Array.from(value, (character) => character.charCodeAt(0));

function expectRejected(label, input) {
  try {
    imageSize(input);
  } catch (error) {
    if (error instanceof TypeError) {
      return;
    }
    throw new Error(`${label} rejected with an unexpected error: ${error}`);
  }
  throw new Error(`${label} was accepted`);
}

const malformedIcns = new Uint8Array(16);
malformedIcns.set(ascii('icns'), 0);
malformedIcns.set([0, 0, 0, 16], 4);
malformedIcns.set(ascii('ic07'), 8);
expectRejected('zero-length ICNS entry', malformedIcns);

const malformedBox = new Uint8Array(16);
malformedBox.set([0, 0, 0, 0], 0);
malformedBox.set(ascii('ftyp'), 4);
malformedBox.set(ascii('heic'), 8);
expectRejected('zero-length HEIF/JXL box', malformedBox);

const validPng = new Uint8Array(28);
validPng.set([137, 80, 78, 71, 13, 10, 26, 10], 0);
validPng.set([0, 0, 0, 13], 8);
validPng.set(ascii('IHDR'), 12);
validPng.set([0, 0, 0, 2], 16);
validPng.set([0, 0, 0, 3], 20);
const dimensions = imageSize(validPng);
if (dimensions.width !== 2 || dimensions.height !== 3) {
  throw new Error(`valid PNG dimensions changed: ${JSON.stringify(dimensions)}`);
}

console.log('image-size security checks passed');
