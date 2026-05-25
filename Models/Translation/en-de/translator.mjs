import { env, pipeline } from '@xenova/transformers';
import { argv, stdin, stdout, stderr, exit } from 'node:process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { basename, dirname, join } from 'node:path';
import { cpus } from 'node:os';

const modelDir = dirname(fileURLToPath(import.meta.url));
const modelName = basename(modelDir).toLowerCase();
const modelId = `Xenova/opus-mt-${modelName}`;
env.cacheDir = join(modelDir, 'cache');
env.allowLocalModels = true;
env.allowRemoteModels = false;
env.backends.onnx.wasm.wasmPaths = join(modelDir, 'node_modules', '@xenova', 'transformers', 'dist') + '\\';
env.backends.onnx.wasm.numThreads = Math.max(1, Math.min(4, cpus().length));

const translationCache = new Map();
const maxCacheEntries = 600;
const maxUncachedChunksPerRequest = 18;
const requestedEngine = readArgumentValue('--engine', 'cpu').toLowerCase();
if (requestedEngine === 'directml') {
  process.env.ANEVRED_DISABLE_WASM_FALLBACK = '1';
  stderr.write('DirectML requested. The patched ONNX backend will prefer the dml execution provider when available.\n');
}

async function readStdin() {
  const chunks = [];
  for await (const chunk of stdin) {
    chunks.push(chunk);
  }

  return Buffer.concat(chunks).toString('utf8').trim();
}

function chunkText(text) {
  const blocks = text
    .replace(/\r\n/g, '\n')
    .split(/\n{2,}/)
    .map((item) => item.trim())
    .filter(Boolean);

  const chunks = [];
  for (const block of blocks) {
    const units = block
      .split(/\n|(?<=[.!?])\s+/)
      .map((item) => item.trim())
      .filter(Boolean);

    let current = '';
    for (const unit of units.length ? units : [block]) {
      const parts = hardSplit(unit, 160);
      for (const part of parts) {
        if ((current + ' ' + part).trim().length > 220 && current.length > 0) {
          chunks.push(current);
          current = part;
        } else {
          current = (current + ' ' + part).trim();
        }
      }
    }

    if (current.length > 0) {
      chunks.push(current);
    }

    chunks.push('');
  }

  while (chunks.length > 0 && chunks[chunks.length - 1] === '') {
    chunks.pop();
  }

  return chunks.slice(0, 18);
}

function hardSplit(text, maxLength) {
  if (text.length <= maxLength) {
    return [text];
  }

  const words = text.split(/\s+/).filter(Boolean);
  const chunks = [];
  let current = '';
  for (const word of words) {
    if ((current + ' ' + word).trim().length > maxLength && current.length > 0) {
      chunks.push(current);
      current = word;
    } else {
      current = (current + ' ' + word).trim();
    }
  }

  if (current.length > 0) {
    chunks.push(current);
  }

  return chunks;
}

try {
  stderr.write(`ANEVRED translator loading: ${modelId}\n`);
  const translator = await pipeline('translation', modelId);

  if (argv.includes('--worker')) {
    stderr.write('ANEVRED translator worker ready\n');
    const reader = createInterface({ input: stdin, crlfDelay: Infinity });
    for await (const line of reader) {
      if (!line.trim()) {
        continue;
      }

      let request;
      try {
        request = JSON.parse(line);
      } catch (error) {
        stdout.write(JSON.stringify({ id: '', text: '', error: 'invalid json' }) + '\n');
        continue;
      }

      if (request?.command === 'exit') {
        exit(0);
      }

      const id = String(request?.id ?? '');
      try {
        const text = await translateText(translator, String(request?.text ?? ''));
        stdout.write(JSON.stringify({ id, text, error: '' }) + '\n');
      } catch (error) {
        stdout.write(JSON.stringify({ id, text: '', error: String(error?.message ?? error) }) + '\n');
      }
    }
  } else {
    const source = await readStdin();
    if (!source) {
      exit(0);
    }

    stdout.write(await translateText(translator, source));
  }
} catch (error) {
  stderr.write(String(error?.stack ?? error));
  exit(1);
}

async function translateText(translator, source) {
  const startedAt = Date.now();
  const chunks = chunkText(source);
  stderr.write(`ANEVRED translator request: sourceChars=${source.length}, chunks=${chunks.filter(Boolean).length}\n`);
  const missing = [];
  const missingKeys = [];

  for (const chunk of chunks) {
    if (!chunk) {
      continue;
    }

    const key = normalizeCacheKey(chunk);
    if (!translationCache.has(key)) {
      missing.push(chunk);
      missingKeys.push(key);
    }
  }

  if (missing.length > 0) {
    const limitedMissing = missing.slice(0, maxUncachedChunksPerRequest);
    const limitedKeys = missingKeys.slice(0, maxUncachedChunksPerRequest);
    stderr.write(`ANEVRED translator uncached chunks: ${limitedMissing.length}/${missing.length}\n`);
    for (let start = 0; start < limitedMissing.length; start += 2) {
      const batch = limitedMissing.slice(start, start + 2);
      const keys = limitedKeys.slice(start, start + 2);
      stderr.write(`ANEVRED translator batch: ${start + 1}-${start + batch.length}/${limitedMissing.length}\n`);
      const results = await translator(batch, {
        max_new_tokens: 96,
      });

      const normalizedResults = Array.isArray(results) ? results : [results];
      for (let index = 0; index < keys.length; index += 1) {
        const item = normalizedResults[index];
        const value = Array.isArray(item)
          ? item?.[0]?.translation_text?.trim() ?? ''
          : item?.translation_text?.trim() ?? '';
        setCachedTranslation(keys[index], value);
      }
    }
  }

  const translated = [];
  for (const chunk of chunks) {
    if (!chunk) {
      translated.push('');
      continue;
    }

    const cached = translationCache.get(normalizeCacheKey(chunk));
    if (cached !== undefined) {
      translated.push(cached);
    }
  }

  const result = translated.join('\n\n').replace(/\n{3,}/g, '\n\n').trim();
  stderr.write(`ANEVRED translator done: resultChars=${result.length}, elapsedMs=${Date.now() - startedAt}\n`);
  return result;
}

function readArgumentValue(name, fallback) {
  const index = argv.indexOf(name);
  if (index < 0 || index + 1 >= argv.length) {
    return fallback;
  }

  return argv[index + 1] ?? fallback;
}

function normalizeCacheKey(text) {
  return text.replace(/\s+/g, ' ').trim();
}

function setCachedTranslation(key, value) {
  if (translationCache.size >= maxCacheEntries) {
    const firstKey = translationCache.keys().next().value;
    if (firstKey) {
      translationCache.delete(firstKey);
    }
  }

  translationCache.set(key, value);
}
