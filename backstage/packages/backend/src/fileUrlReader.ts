/*
 * A minimal read-only `file://` URL reader for the babelstone catalogue portal (ADR-IC-015 §9).
 *
 * In plain English: Backstage renders the governed catalogue from files baked into the image,
 * and each `kind: API` entity points at its spec file with a relative `spec.definition.$text`
 * (e.g. `./events/DepositConstituted.asyncapi.yaml`). To resolve those relative refs, Backstage
 * must register the catalogue as a `type: url` location with a proper URL base — a `file://` URL —
 * because relative resolution is `new URL(value, base)` and a bare filesystem path
 * (`/catalog/catalog-info.yaml`) has no URL scheme, so it throws `Invalid URL`. The stock backend
 * ships NO reader for the `file:` scheme (only SCM + fetch readers), so those `file://` reads are
 * rejected outright. This module supplies that missing reader, registered through the documented
 * `urlReaderFactoriesServiceRef` extension point so it is prepended to the default readers.
 *
 * Why not inline the schemas at build instead? ADR-IC-015 Decision §2 and the descriptor's own
 * comment require the schema is "NEVER inlined" — the catalogue references its sources by `$text`
 * and never restates them. Reading the baked files at runtime keeps that non-inlining discipline
 * and the no-drift invariant (the descriptor tree is read verbatim from the image, not GitHub, so
 * there is no network dependency and no drift from the committed sources the image was built from).
 */
import { readFile } from 'node:fs/promises';
import { Readable } from 'node:stream';
import { fileURLToPath } from 'node:url';
import {
  coreServices,
  createServiceFactory,
  UrlReaderService,
  UrlReaderServiceReadTreeResponse,
  UrlReaderServiceReadUrlOptions,
  UrlReaderServiceReadUrlResponse,
  UrlReaderServiceSearchOptions,
  UrlReaderServiceSearchResponse,
} from '@backstage/backend-plugin-api';
import { urlReaderFactoriesServiceRef } from '@backstage/backend-defaults/urlReader';
import { NotAllowedError, NotFoundError, toError } from '@backstage/errors';

// The read-only baked catalogue roots. backstage/Dockerfile COPYs the governed tree here:
//   contracts/catalog/  -> /catalog   (catalog-info.yaml + events/ + reconciliation/)
//   contracts/openapi/  -> /openapi   (specs/ + internal/)
// Any file:// read whose resolved path is outside these roots is refused: this reader exists ONLY
// to resolve the governed descriptor's relative $text refs from the image, never to read arbitrary
// host files (defence-in-depth for the zero-trust runtime posture the Deployment pins).
const ALLOWED_ROOTS = ['/catalog/', '/openapi/'];

class FileUrlReader implements UrlReaderService {
  /**
   * A {@link ReaderFactory}: claims every `file:` URL. The path allow-list is enforced in
   * {@link FileUrlReader.readUrl} so an out-of-root ref fails with a clear error rather than a
   * confusing "no reader matched" from the mux.
   */
  static readonly factory = () => [
    {
      reader: new FileUrlReader(),
      predicate: (url: URL) => url.protocol === 'file:',
    },
  ];

  async readUrl(
    url: string,
    _options?: UrlReaderServiceReadUrlOptions,
  ): Promise<UrlReaderServiceReadUrlResponse> {
    const filePath = fileURLToPath(url);
    if (!ALLOWED_ROOTS.some(root => filePath.startsWith(root))) {
      throw new NotAllowedError(
        `file:// reads are restricted to the baked catalogue roots (${ALLOWED_ROOTS.join(
          ', ',
        )}); refused ${filePath}`,
      );
    }
    let buffer: Buffer;
    try {
      buffer = await readFile(filePath);
    } catch (error) {
      throw new NotFoundError(`Could not read ${url}, ${error}`);
    }
    return {
      buffer: async () => buffer,
      stream: () => Readable.from(buffer),
    };
  }

  async readTree(): Promise<UrlReaderServiceReadTreeResponse> {
    // The catalogue references single files only (every $text / location target is one file), so
    // tree reads are never issued; fail loud if that assumption ever changes.
    throw new Error('FileUrlReader does not support readTree');
  }

  async search(
    url: string,
    options?: UrlReaderServiceSearchOptions,
  ): Promise<UrlReaderServiceSearchResponse> {
    // A `type: url` catalogue location is read via search(), not readUrl() — so this MUST work for
    // the single registered location `file:///catalog/catalog-info.yaml`, or nothing ingests.
    // The catalogue's targets are exact single files (no glob), so mirror the stock FetchUrlReader:
    // delegate an exact URL to readUrl and wrap it as a one-file result; surface a missing file as
    // an empty result rather than an error.
    const { pathname } = new URL(url);
    if (pathname.match(/[*?]/)) {
      throw new Error('FileUrlReader does not support glob search patterns');
    }
    try {
      const data = await this.readUrl(url, options);
      return {
        files: [{ url, content: data.buffer, lastModifiedAt: data.lastModifiedAt }],
        etag: data.etag ?? '',
      };
    } catch (e) {
      const error = toError(e);
      if (error.name === 'NotFoundError') {
        return { files: [], etag: '' };
      }
      throw error;
    }
  }
}

/**
 * Registers {@link FileUrlReader} as an additional URL-reader factory (multiton), prepended to the
 * backend-default readers — the documented `urlReaderFactoriesServiceRef` extension point.
 */
export const fileUrlReaderServiceFactory = createServiceFactory({
  service: urlReaderFactoriesServiceRef,
  deps: { logger: coreServices.logger },
  async factory({ logger }) {
    logger.info(
      `Registered read-only file:// URL reader for the baked catalogue roots (${ALLOWED_ROOTS.join(
        ', ',
      )})`,
    );
    return FileUrlReader.factory;
  },
});
