import { env, pipeline } from '@xenova/transformers';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const modelDir = dirname(fileURLToPath(import.meta.url));
env.cacheDir = join(modelDir, 'cache');
env.allowLocalModels = true;
env.allowRemoteModels = true;

console.log('Downloading local EN-RU translation model...');
const translator = await pipeline('translation', 'Xenova/opus-mt-en-ru');
const result = await translator('Hello pilot. Launch game.', { max_new_tokens: 64 });
console.log(result?.[0]?.translation_text ?? '');
console.log('Done. ANEVRED can now translate offline with this cached model.');
